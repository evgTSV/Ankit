/* C interface of ankit_rslib, see src/lib.rs for the full contract. */
#ifndef ANKIT_RSLIB_H
#define ANKIT_RSLIB_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define ANKIT_OK 0
#define ANKIT_BACKEND_ERROR 1
#define ANKIT_PANIC 2
#define ANKIT_INVALID_ARGUMENT 3

typedef struct AnkitBackend AnkitBackend;

/* Allocated by the library, release with ankit_buffer_free. */
typedef struct AnkitBuffer {
    uint8_t *data;
    size_t len;
} AnkitBuffer;

const char *ankit_buildhash(void);

int32_t ankit_initialize_logging(const uint8_t *path, size_t path_len, AnkitBuffer *out_error);

/* init: encoded anki.backend.BackendInit */
int32_t ankit_backend_open(const uint8_t *init, size_t init_len, AnkitBackend **out_backend,
                           AnkitBuffer *out_error);

void ankit_backend_close(AnkitBackend *backend);

/* out: encoded response on ANKIT_OK, encoded anki.backend.BackendError on ANKIT_BACKEND_ERROR */
int32_t ankit_backend_command(const AnkitBackend *backend, uint32_t service, uint32_t method,
                              const uint8_t *input, size_t input_len, AnkitBuffer *out);

/* JSON in, JSON out; errors as encoded anki.backend.BackendError */
int32_t ankit_backend_db_command(const AnkitBackend *backend, const uint8_t *input, size_t input_len,
                                 AnkitBuffer *out);

void ankit_buffer_free(AnkitBuffer buffer);

#ifdef __cplusplus
}
#endif

#endif /* ANKIT_RSLIB_H */
