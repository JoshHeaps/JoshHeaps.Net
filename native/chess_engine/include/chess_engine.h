/* chess_engine.h - C ABI for a swappable chess engine.
 *
 * Contract: FEN string in, UCI move string out (e.g. "e2e4", "e7e8q").
 * The C# host (CustomChessEngine) owns all buffers. The engine NEVER allocates
 * memory that the host must free. Functions are thread-compatible per-handle only:
 * do NOT call two functions on the SAME handle concurrently. Different handles
 * are independent.
 */
#ifndef CHESS_ENGINE_H
#define CHESS_ENGINE_H

#include <stddef.h>

/* ---- Export / calling-convention macro (MSVC + GCC/Clang) ---- */
#if defined(_WIN32)
  #ifdef CHESS_ENGINE_BUILD
    #define CHESS_API __declspec(dllexport)
  #else
    #define CHESS_API __declspec(dllimport)
  #endif
  #define CHESS_CALL __cdecl          /* explicit; matches C# CallingConvention.Cdecl */
#else
  #define CHESS_API __attribute__((visibility("default")))
  #define CHESS_CALL                  /* SysV default; no decoration needed */
#endif

#ifdef __cplusplus
extern "C" {                          /* prevent C++ name mangling */
#endif

/* Opaque handle. The host treats this as a token and never dereferences it.
 * Internally it points to your engine state object. */
typedef struct ChessEngine* EngineHandle;

/* Opaque per-game training accumulator (see the learned-weights ABI at the bottom). */
typedef struct Trainer* TrainerHandle;

/* Return codes. 0 == success; negative == error. Keep these values stable. */
enum {
    CHESS_OK              =  0,
    CHESS_ERR_NULL_HANDLE = -1,  /* handle was null/invalid              */
    CHESS_ERR_BAD_FEN     = -2,  /* fen failed to parse                  */
    CHESS_ERR_NO_MOVE     = -3,  /* no legal move (mate/stalemate)       */
    CHESS_ERR_BUFFER      = -4,  /* out_buf too small for the move + NUL */
    CHESS_ERR_INTERNAL    = -5   /* unexpected engine failure            */
};

/* Create an engine instance.
 * options: optional null-terminated UTF-8 config string (may be NULL),
 *          e.g. "skill=20;hash=256". Parse however you like; ignore for now.
 * returns: a valid EngineHandle, or NULL on allocation failure. */
CHESS_API EngineHandle CHESS_CALL engine_create(const char* options);

/* Set a single option by name (optional; may no-op for now).
 * returns CHESS_OK or a negative code. */
CHESS_API int CHESS_CALL engine_set_option(EngineHandle engine,
                                           const char* name,
                                           const char* value);

/* Compute the best move for the given position.
 *   engine  : handle from engine_create.
 *   fen     : null-terminated UTF-8 FEN of the position to move from.
 *   history : optional null-terminated UTF-8 list of the prior positions since the
 *             last irreversible move (capture/pawn move), one FEN per line, oldest
 *             first, NOT including `fen`. Lets the engine detect threefold/50-move
 *             draws that the FEN alone can't carry. May be NULL or empty.
 *   out_buf : host-owned buffer the engine writes the UCI move into,
 *             as a null-terminated ASCII string (e.g. "e2e4\0").
 *   out_len : capacity of out_buf in bytes (host passes >= 8).
 * returns CHESS_OK on success (out_buf now holds the move), else negative.
 * MUST NOT write more than out_len bytes including the NUL terminator. */
CHESS_API int CHESS_CALL engine_best_move(EngineHandle engine,
                                          const char* fen,
                                          const char* history,
                                          char* out_buf,
                                          int   out_len);

/* Write the engine version string into out_buf (null-terminated).
 * returns CHESS_OK or CHESS_ERR_BUFFER. */
CHESS_API int CHESS_CALL engine_version(char* out_buf, int out_len);

/* Destroy an instance created by engine_create. Safe to call with NULL. */
CHESS_API void CHESS_CALL engine_destroy(EngineHandle engine);

/* ---- Learned-weights / training ABI ----
 * The learned engine's weights live process-globally here. The host orchestrates games but
 * owns no chess logic: it points the engine at the weights file, records each played
 * position, and applies the game's result. */

/* Load the global learned weights from `path` and remember it for later saves. Idempotent;
 * a missing/short file leaves the weights neutral. Call once before learned play/training. */
CHESS_API void CHESS_CALL learned_load(const char* path);

/* Copy the global weights out for visualization: 6*64 midgame + 6*64 endgame (PAWN..KING,
 * squares 0..63) + feature weights. Returns the count written, or CHESS_ERR_BUFFER if
 * out_len is too small (needs >= 776). */
CHESS_API int CHESS_CALL weights_snapshot(int* out, int out_len);

/* Create / destroy a per-game training accumulator. Safe to destroy NULL. */
CHESS_API TrainerHandle CHESS_CALL trainer_create(void);
CHESS_API void          CHESS_CALL trainer_destroy(TrainerHandle trainer);

/* Record one played position (post-move FEN) into the accumulator. */
CHESS_API void CHESS_CALL trainer_record(TrainerHandle trainer, const char* fen);

/* Apply a finished game's outcome to the global weights and save: rewards the winner's
 * occupied squares / features and punishes the loser's, scaled by `weight` (e.g. 0.5 for a
 * material-imbalance draw). winner: 0 = white, 1 = black. */
CHESS_API void CHESS_CALL trainer_apply(TrainerHandle trainer, int winner, double weight);

#ifdef __cplusplus
}
#endif
#endif /* CHESS_ENGINE_H */
