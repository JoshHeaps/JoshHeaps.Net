/* learned_model.cpp - global learned weights and the per-game trainer.
 *
 * The model is trained by WIN RATE, not by additive nudges. For every (piece, phase,
 * square) we keep two running totals across all games: `win` (turns the piece spent there in
 * games that side won) and `total` (turns spent there in any game). The stored weight is
 * derived: weight = (2·win/total − 1)·scale, i.e. win-rate 0→−scale, 0.5→0, 1→+scale. Same
 * for each feature, totalling its activation per turn. This focuses training on "how much
 * time on this square correlates with winning" and is far less volatile than per-game nudges.
 *
 * The counters are the persistent source of truth (saved to / loaded from disk); the integer
 * weight tables in `g_weights` are recomputed from them. See learned_model.h for the public
 * surface; feature/phase math is shared from eval.cpp. */
#include "learned_model.h"
#include "chess_engine.h"        /* CHESS_ERR_BUFFER */
#include "eval.h"
#include "position.h"

#include <cmath>
#include <cstring>
#include <fstream>
#include <mutex>
#include <new>
#include <string>

/* Win-rate → weight scale. A 100%-win square/feature reaches +scale, a 0%-win one −scale,
 * matching the ranges the additive trainer used to clamp at (squares ±250, features ±500). */
static constexpr double SQ_WEIGHT_SCALE   = 250.0;
static constexpr double FEAT_WEIGHT_SCALE = 500.0;

/* On-disk format version, stored as the file's first token. On load, a missing or mismatched
 * version means the file is stale (old layout / different feature set): its contents are
 * wiped (the file itself is kept) and training restarts from neutral. Bump this whenever the
 * counter layout or feature set changes — it replaces having to delete the file by hand. */
static constexpr int LEARNED_VERSION = 1;

/* Derived integer weight tables, read by eval (snapshotted per engine handle) and the viz.
 * Recomputed from g_counts whenever the counters change. White-relative (black indexes
 * sq ^ 56); mg/eg blended by game phase. */
struct LearnedWeights {
    int mg[chess::PIECE_TYPE_NB][64] = {};
    int eg[chess::PIECE_TYPE_NB][64] = {};
    int featW[FEATURE_NB] = {};
};

/* The persistent training counters: the single source of truth. `win` is credited only to
 * the winning side; `total` to both sides (scaled by the outcome weight). */
struct WinCounters {
    double winMg[chess::PIECE_TYPE_NB][64] = {};
    double totMg[chess::PIECE_TYPE_NB][64] = {};
    double winEg[chess::PIECE_TYPE_NB][64] = {};
    double totEg[chess::PIECE_TYPE_NB][64] = {};
    double winFeat[FEATURE_NB] = {};
    double totFeat[FEATURE_NB] = {};
};

static LearnedWeights g_weights;
static WinCounters    g_counts;
static std::mutex     g_weightsMutex;
static std::string    g_weightsPath;

/* win/total → stored weight: win-rate 0 → −scale, 0.5 → 0, 1 → +scale. An untouched
 * (total == 0) square/feature is neutral. */
static int derive(double win, double total, double scale) {
    if (total <= 0.0) return 0;
    double rate = win / total;
    return int(std::lround((2.0 * rate - 1.0) * scale));
}

/* Recompute every derived weight from the counters. Caller holds g_weightsMutex. */
static void recompute_weights() {
    for (int pt = chess::PAWN; pt <= chess::KING; ++pt)
        for (int sq = 0; sq < 64; ++sq) {
            g_weights.mg[pt][sq] = derive(g_counts.winMg[pt][sq], g_counts.totMg[pt][sq], SQ_WEIGHT_SCALE);
            g_weights.eg[pt][sq] = derive(g_counts.winEg[pt][sq], g_counts.totEg[pt][sq], SQ_WEIGHT_SCALE);
        }
    for (int i = 0; i < FEATURE_NB; ++i)
        g_weights.featW[i] = derive(g_counts.winFeat[i], g_counts.totFeat[i], FEAT_WEIGHT_SCALE);
}

static void save_global_weights();   /* defined below; load rewrites stale files via it */

/* On-disk format: LEARNED_VERSION as the first token, then the counters as whitespace doubles
 * in this order — winMg, totMg, winEg, totEg (each 6*64, PAWN..KING, squares 0..63), then
 * winFeat, totFeat (each FEATURE_NB). If the version is missing/wrong or the file is short
 * (old format, corrupt, or absent), the counters are left neutral and the file is rewritten
 * blank-but-versioned — clearing stale contents while keeping the file. Caller holds the lock. */
static void load_global_weights(const char* path) {
    WinCounters loaded{};
    bool ok = false;

    if (path && *path) {
        std::ifstream f(path);
        if (f) {
            int version = 0;
            if ((f >> version) && version == LEARNED_VERSION) {
                auto readTable = [&](double t[chess::PIECE_TYPE_NB][64]) -> bool {
                    for (int pt = chess::PAWN; pt <= chess::KING; ++pt)
                        for (int sq = 0; sq < 64; ++sq)
                            if (!(f >> t[pt][sq])) return false;
                    return true;
                };
                ok = readTable(loaded.winMg) && readTable(loaded.totMg)
                  && readTable(loaded.winEg) && readTable(loaded.totEg);
                for (int i = 0; ok && i < FEATURE_NB; ++i) if (!(f >> loaded.winFeat[i])) ok = false;
                for (int i = 0; ok && i < FEATURE_NB; ++i) if (!(f >> loaded.totFeat[i])) ok = false;
            }
        }
    }

    g_counts = ok ? loaded : WinCounters{};
    recompute_weights();

    /* Stale / wrong-version / unreadable: wipe the file's contents (keep the file) by
     * rewriting it blank-but-versioned, so the next load matches and we never reread garbage. */
    if (!ok)
        save_global_weights();
}

/* Persist g_counts to g_weightsPath in the format load_global_weights reads. Caller holds the lock. */
static void save_global_weights() {
    if (g_weightsPath.empty()) return;
    std::ofstream f(g_weightsPath);
    if (!f) return;

    f << LEARNED_VERSION << '\n';

    auto writeTable = [&](const double t[chess::PIECE_TYPE_NB][64]) {
        for (int pt = chess::PAWN; pt <= chess::KING; ++pt)
            for (int sq = 0; sq < 64; ++sq) f << t[pt][sq] << (sq == 63 ? '\n' : ' ');
    };
    writeTable(g_counts.winMg); writeTable(g_counts.totMg);
    writeTable(g_counts.winEg); writeTable(g_counts.totEg);
    for (int i = 0; i < FEATURE_NB; ++i) f << g_counts.winFeat[i] << (i == FEATURE_NB - 1 ? '\n' : ' ');
    for (int i = 0; i < FEATURE_NB; ++i) f << g_counts.totFeat[i] << (i == FEATURE_NB - 1 ? '\n' : ' ');
}

/* ---- Per-game training accumulator ----------------------------------------------------
 * Records, per ply, where each side's pieces sat (split into midgame/endgame by phase) and
 * each side's feature activations. learned::apply folds these per-side totals into the global
 * win/total counters. Squares are white-relative (black indexes sq ^ 56), so a side's tally
 * lines up with the shared white-relative table. */
struct Trainer {
    double mgOcc[chess::COLOR_NB][chess::PIECE_TYPE_NB][64] = {};
    double egOcc[chess::COLOR_NB][chess::PIECE_TYPE_NB][64] = {};
    double featAcc[chess::COLOR_NB][FEATURE_NB] = {};
};

namespace learned {

void load(const char* path) {
    std::lock_guard<std::mutex> lock(g_weightsMutex);
    g_weightsPath = path ? path : "";
    load_global_weights(path);
}

int snapshot(int* out, int out_len) {
    const int need = 6 * 64 * 2 + FEATURE_NB;     /* mg + eg (PAWN..KING) + features */
    if (!out || out_len < need) return CHESS_ERR_BUFFER;

    std::lock_guard<std::mutex> lock(g_weightsMutex);
    int n = 0;
    for (int pt = chess::PAWN; pt <= chess::KING; ++pt)
        for (int sq = 0; sq < 64; ++sq) out[n++] = g_weights.mg[pt][sq];
    for (int pt = chess::PAWN; pt <= chess::KING; ++pt)
        for (int sq = 0; sq < 64; ++sq) out[n++] = g_weights.eg[pt][sq];
    for (int i = 0; i < FEATURE_NB; ++i) out[n++] = g_weights.featW[i];
    return n;
}

void copy_weights_to(EvalParams& ep) {
    std::lock_guard<std::mutex> lock(g_weightsMutex);
    std::memcpy(ep.mg,    g_weights.mg,    sizeof ep.mg);
    std::memcpy(ep.eg,    g_weights.eg,    sizeof ep.eg);
    std::memcpy(ep.featW, g_weights.featW, sizeof ep.featW);
}

Trainer* create() {
    return new (std::nothrow) Trainer();
}

void destroy(Trainer* t) {
    delete t;                                             /* delete nullptr is safe */
}

void record(Trainer* t, const char* fen) {
    if (!t || !fen || !*fen) return;

    chess::Position pos = chess::Position::from_fen(fen);
    double phase = game_phase(pos);

    /* Per-square occupancy, split into midgame/endgame by phase, white-relative. */
    chess::Bitboard occ = pos.pieces();
    while (occ) {
        chess::Square s  = chess::pop_lsb(occ);
        chess::Piece  pc = pos.piece_on(s);
        chess::Color  c  = chess::color_of(pc);
        chess::PieceType pt = chess::type_of(pc);
        int relSq = (c == chess::WHITE) ? int(s) : (int(s) ^ 56);
        t->mgOcc[c][pt][relSq] += (1.0 - phase);
        t->egOcc[c][pt][relSq] += phase;
    }

    /* Per-side feature activations. */
    double w[FEATURE_NB], b[FEATURE_NB];
    compute_features(pos, chess::WHITE, phase, w);
    compute_features(pos, chess::BLACK, phase, b);
    for (int i = 0; i < FEATURE_NB; ++i) {
        t->featAcc[chess::WHITE][i] += w[i];
        t->featAcc[chess::BLACK][i] += b[i];
    }
}

void apply(Trainer* t, int winner, double weight) {
    if (!t) return;

    std::lock_guard<std::mutex> lock(g_weightsMutex);

    /* Fold each side's per-game tallies into the global counters: both sides credit `total`,
     * only the winner credits `win`, each scaled by the outcome weight (1.0 for a decisive
     * game, 0.5 for a material-imbalance draw). */
    for (int s = 0; s < chess::COLOR_NB; ++s) {
        bool isWinner = (s == winner);

        for (int pt = chess::PAWN; pt <= chess::KING; ++pt)
            for (int sq = 0; sq < 64; ++sq) {
                double mg = weight * t->mgOcc[s][pt][sq];
                double eg = weight * t->egOcc[s][pt][sq];
                g_counts.totMg[pt][sq] += mg;
                g_counts.totEg[pt][sq] += eg;
                if (isWinner) {
                    g_counts.winMg[pt][sq] += mg;
                    g_counts.winEg[pt][sq] += eg;
                }
            }

        for (int i = 0; i < FEATURE_NB; ++i) {
            double f = weight * t->featAcc[s][i];
            g_counts.totFeat[i] += f;
            if (isWinner) g_counts.winFeat[i] += f;
        }
    }

    recompute_weights();
    save_global_weights();
}

}  // namespace learned
