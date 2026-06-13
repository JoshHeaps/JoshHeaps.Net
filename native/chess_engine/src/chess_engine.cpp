/* chess_engine.cpp - the DLL boundary (extern "C" ABI).
 *
 * This file is intentionally thin: it owns only the C ABI surface and the FEN/UCI string
 * marshalling at the managed boundary. The real work lives in the modules it delegates to:
 *   - eval.{h,cpp}          : classic + learned evaluation, feature computation
 *   - search.{h,cpp}        : transposition table, move ordering, negamax + iterative deepening
 *   - learned_model.{h,cpp} : global learned weights (state/persistence) and the trainer
 * The managed side crosses this boundary once per move; everything below it stays native.
 */
#ifndef CHESS_ENGINE_BUILD
#define CHESS_ENGINE_BUILD            /* fallback when not building via CMake (which defines it) */
#endif

#include "chess_engine.h"
#include "bitboard.h"
#include "zobrist.h"
#include "position.h"
#include "movegen.h"
#include "uci.h"
#include "eval.h"
#include "search.h"
#include "learned_model.h"

#include <cstdlib>
#include <cstring>
#include <memory>
#include <new>
#include <string>
#include <vector>

/* Internal engine state. One ChessEngine = one game. The transposition table is NOT here:
 * it is the shared table owned by search.cpp. */
struct ChessEngine {
    int        skill = 20;   /* 1..20 from the UI; controls search depth */
    EvalParams eval;         /* which evaluation the search uses, plus any learned weights */
};

static int copy_out(const char* src, char* out_buf, int out_len) {
    if (!out_buf || out_len <= 0) return CHESS_ERR_BUFFER;
    const size_t need = std::strlen(src) + 1;            /* + NUL */
    if (need > static_cast<size_t>(out_len)) return CHESS_ERR_BUFFER;
    std::memcpy(out_buf, src, need);
    return CHESS_OK;
}

/* Attack tables and Zobrist keys are global and read-only after this runs. */
static void ensure_initialized() {
    static bool done = false;
    if (done) return;
    chess::init_bitboards();
    chess::Zobrist::init();
    done = true;
}

/* Pulls "skill=N" out of the engine_create options string; clamps to the UI's 1..20. */
static int parse_skill(const char* options, int fallback) {
    if (!options) return fallback;
    const char* p = std::strstr(options, "skill=");
    if (!p) return fallback;
    int v = std::atoi(p + 6);
    return v < 1 ? 1 : v > 20 ? 20 : v;
}

/* "variant=learned" in the options selects the learned eval; anything else is classic. */
static int parse_variant(const char* options) {
    if (!options) return EVAL_CLASSIC;
    const char* p = std::strstr(options, "variant=");
    if (!p) return EVAL_CLASSIC;
    return std::strncmp(p + 8, "learned", 7) == 0 ? EVAL_LEARNED : EVAL_CLASSIC;
}

extern "C" {

CHESS_API EngineHandle CHESS_CALL engine_create(const char* options) {
    ensure_initialized();
    auto* e = new (std::nothrow) ChessEngine();
    if (!e) return nullptr;
    e->skill = parse_skill(options, e->skill);
    e->eval.variant = parse_variant(options);
    if (e->eval.variant == EVAL_LEARNED)
        learned::copy_weights_to(e->eval);   /* stable per-handle copy of the global weights */
    return e;
}

CHESS_API int CHESS_CALL engine_set_option(EngineHandle engine,
                                           const char* /*name*/,
                                           const char* /*value*/) {
    if (!engine) return CHESS_ERR_NULL_HANDLE;
    return CHESS_OK;                                      /* TODO: store options */
}

CHESS_API int CHESS_CALL engine_best_move(EngineHandle engine,
                                          const char* fen,
                                          const char* history,
                                          char* out_buf,
                                          int   out_len) {
    if (!engine)       return CHESS_ERR_NULL_HANDLE;
    if (!fen || !*fen) return CHESS_ERR_BAD_FEN;

    auto held = std::make_unique<chess::Position>(chess::Position::from_fen(fen));
    chess::Position& pos = *held;

    /* Seed the prior positions (one FEN per line) so is_draw() sees repetitions and
     * the 50-move count that the current FEN alone can't express. */
    if (history && *history) {
        std::vector<uint64_t> priorKeys;
        const char* p = history;
        while (*p) {
            const char* nl = std::strchr(p, '\n');
            size_t len = nl ? static_cast<size_t>(nl - p) : std::strlen(p);
            if (len > 0)
                priorKeys.push_back(chess::Position::from_fen(std::string(p, len)).key());
            if (!nl) break;
            p = nl + 1;
        }
        if (!priorKeys.empty())
            pos.seed_history(priorKeys.data(), static_cast<int>(priorKeys.size()));
    }

    chess::Move best = find_best_move(pos, engine->eval, engine->skill);
    if (best == chess::MOVE_NONE)
        return CHESS_ERR_NO_MOVE;

    return copy_out(chess::move_to_uci(best).c_str(), out_buf, out_len);
}

CHESS_API int CHESS_CALL engine_version(char* out_buf, int out_len) {
    return copy_out("custom-engine 0.1.0", out_buf, out_len);
}

CHESS_API void CHESS_CALL engine_destroy(EngineHandle engine) {
    delete engine;                                        /* delete nullptr is safe */
}

/* ---- Learned-weights / training C ABI --------------------------------------------------
 * The managed side orchestrates games but owns no chess logic: it tells the engine where to
 * load/save the global weights, records each played position, and applies the result. Each
 * export is a thin pass-through to the learned_model module. */

CHESS_API void CHESS_CALL learned_load(const char* path) {
    learned::load(path);
}

CHESS_API int CHESS_CALL weights_snapshot(int* out, int out_len) {
    return learned::snapshot(out, out_len);
}

CHESS_API TrainerHandle CHESS_CALL trainer_create(void) {
    return learned::create();
}

CHESS_API void CHESS_CALL trainer_record(TrainerHandle t, const char* fen) {
    ensure_initialized();                                 /* mobility needs the attack tables */
    learned::record(t, fen);
}

CHESS_API void CHESS_CALL trainer_apply(TrainerHandle t, int winner, double weight) {
    learned::apply(t, winner, weight);
}

CHESS_API void CHESS_CALL trainer_destroy(TrainerHandle t) {
    learned::destroy(t);                                  /* destroy(nullptr) is safe */
}

} /* extern "C" */
