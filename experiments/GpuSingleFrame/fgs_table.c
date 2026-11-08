#include "fgs_table.h"

#include <errno.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static bool fail(char *error, size_t size, const char *reason)
{
    if (size) snprintf(error, size, "%s", reason);
    return false;
}

/* Parse strict decimal tokens; no locale-dependent numbers or silent overflow. */
static int integers(const char *line, int64_t *values, int capacity)
{
    int n = 0;
    while (*line) {
        while (*line == ' ' || *line == '\t' || *line == '\r') line++;
        if (!*line || *line == '\n' || *line == '#') break;
        if (n == capacity) return -1;
        errno = 0;
        char *end;
        long long value = strtoll(line, &end, 10);
        if (end == line || errno == ERANGE ||
            (*end != ' ' && *end != '\t' && *end != '\r' && *end != '\n' && *end != 0))
            return -1;
        values[n++] = (int64_t) value;
        line = end;
    }
    return n;
}

bool fgs_parse_table(const char *text, FgsGrainModel *out,
                     char *error, size_t error_size)
{
    if (!text || !out) return fail(error, error_size, "null table/model");
    memset(out, 0, sizeof(*out));
    const char *cursor = text;
    if ((unsigned char)cursor[0] == 0xef &&
        (unsigned char)cursor[1] == 0xbb &&
        (unsigned char)cursor[2] == 0xbf) cursor += 3;
    if (strncmp(cursor, "filmgrn1", 8) ||
        (cursor[8] != '\n' && cursor[8] != '\r'))
        return fail(error, error_size, "expected filmgrn1 header");
    cursor += 8;

    int event_count = 0, parameter_count = 0;
    bool have_points[3] = {false}, have_coefficients[3] = {false};
    while (*cursor) {
        char line[8192];
        const char *eol = strchr(cursor, '\n');
        size_t len = eol ? (size_t)(eol - cursor) : strlen(cursor);
        if (len >= sizeof(line)) return fail(error, error_size, "oversized table line");
        memcpy(line, cursor, len); line[len] = 0;
        cursor = eol ? eol + 1 : cursor + len;

        char *p = line;
        while (*p == ' ' || *p == '\t' || *p == '\r') p++;
        if (*p == 0 || *p == '#') continue;
        char key[8] = {0}; int key_len = 0;
        while (*p && *p != ' ' && *p != '\t' && *p != '\r') {
            if (key_len >= 7) return fail(error, error_size, "unknown table field");
            key[key_len++] = *p++;
        }
        int64_t v[64];
        int count = integers(p, v, 64);
        if (count < 0) return fail(error, error_size, "invalid integer in grain table");
        if (!strcmp(key, "E")) {
            if (++event_count != 1) return fail(error, error_size, "multi-event table unsupported in prototype");
            if (count != 5 || v[0] != 0 || v[1] <= v[0] ||
                v[2] != 1 || v[3] < 0 || v[3] > 65535 || v[4] != 1)
                return fail(error, error_size, "expected one active zero-origin updated event");
            out->seed = (uint16_t)v[3];
        } else if (!strcmp(key, "p")) {
            if (++parameter_count != 1 || count != 12)
                return fail(error, error_size, "invalid p fields");
            if (v[0] < 0 || v[0] > 3 || v[1] < 6 || v[1] > 9 ||
                v[2] < 0 || v[2] > 3 || v[3] < 8 || v[3] > 11 ||
                (v[4] != 0 && v[4] != 1) || (v[5] != 0 && v[5] != 1))
                return fail(error, error_size, "unsupported AV1 grain p values");
            out->lag = (int)v[0]; out->ar_shift = (int)v[1];
            out->grain_shift = (int)v[2]; out->scaling_shift = (int)v[3];
            out->chroma_from_luma = (int)v[4]; out->overlap = (int)v[5];
            for (int ch = 0; ch < 2; ch++) {
                int i = 6 + ch * 3;
                if (v[i] < 0 || v[i] > 255 || v[i+1] < 0 || v[i+1] > 255 ||
                    v[i+2] < 0 || v[i+2] > 511)
                    return fail(error, error_size, "invalid UV multiplier/offset");
                out->uv_mult[ch] = (int)v[i];
                out->uv_luma_mult[ch] = (int)v[i+1];
                out->uv_offset[ch] = (int)v[i+2];
            }
        } else if (!strcmp(key, "sY") || !strcmp(key, "sCb") || !strcmp(key, "sCr")) {
            int plane = !strcmp(key, "sY") ? 0 : !strcmp(key, "sCb") ? 1 : 2;
            int limit = plane == 0 ? 14 : 10;
            if (have_points[plane] || count < 1 || v[0] < 0 || v[0] > limit || count != 1+2*v[0])
                return fail(error, error_size, "invalid scaling point count");
            have_points[plane] = true;
            out->points_count[plane] = (int)v[0];
            int previous = -1;
            for (int i = 0; i < out->points_count[plane]; i++) {
                int64_t x = v[1+2*i], y = v[2+2*i];
                if (x <= previous || x > 255 || y < 0 || y > 255)
                    return fail(error, error_size, "invalid/nonmonotonic scaling point");
                previous = (int)x;
                out->points[plane][i][0] = (uint8_t)x;
                out->points[plane][i][1] = (uint8_t)y;
            }
        } else if (!strcmp(key, "cY") || !strcmp(key, "cCb") || !strcmp(key, "cCr")) {
            int plane = !strcmp(key, "cY") ? 0 : !strcmp(key, "cCb") ? 1 : 2;
            if (have_coefficients[plane] || count > 25)
                return fail(error, error_size, "invalid AR coefficient count");
            have_coefficients[plane] = true;
            out->coefficients_count[plane] = count;
            for (int i=0; i<count; i++) {
                if (v[i] < -128 || v[i] > 127)
                    return fail(error, error_size, "AR coefficient outside signed byte range");
                out->coefficients[plane][i] = (int8_t)v[i];
            }
        } else return fail(error, error_size, "unrecognized table field");
    }
    if (event_count != 1 || parameter_count != 1 ||
        !have_points[0] || !have_points[1] || !have_points[2] ||
        !have_coefficients[0] || !have_coefficients[1] || !have_coefficients[2])
        return fail(error, error_size, "incomplete grain event");

    int luma = 2*out->lag*(out->lag+1), chroma = luma+1;
    if (out->coefficients_count[0] != luma)
        return fail(error, error_size, "luma AR coefficient count mismatched lag");
    for (int ch=1; ch<3; ch++) {
        bool active = out->chroma_from_luma || out->points_count[ch];
        int n = out->coefficients_count[ch];
        if (active && n != chroma)
            return fail(error, error_size, "active chroma AR coefficient count mismatched lag");
        if (!active && n != 0 && n != 1 && n != chroma)
            return fail(error, error_size, "inactive chroma AR coefficient count unsupported");
    }
    if (error_size) error[0] = 0;
    return true;
}

bool fgs_parse_table_at_event(const char *text, uint64_t ticks, FgsGrainModel *out,
                              uint64_t *event_start, char *error, size_t error_size)
{
    if (!text || !out) return fail(error, error_size, "null table/model");
    const char *cursor = text;
    if ((unsigned char)cursor[0] == 0xef &&
        (unsigned char)cursor[1] == 0xbb &&
        (unsigned char)cursor[2] == 0xbf) cursor += 3;
    if (strncmp(cursor, "filmgrn1", 8) ||
        (cursor[8] != '\n' && cursor[8] != '\r'))
        return fail(error, error_size, "expected filmgrn1 header");

    const char *model = NULL, *selected_model = NULL;
    int64_t selected_seed = -1, selected_start = -1, last_end = 0;
    bool found = false, have_event = false;
    while (*cursor) {
        const char *line_start = cursor;
        const char *end = strchr(cursor, '\n');
        size_t len = end ? (size_t)(end - cursor) : strlen(cursor);
        if (len > 8191) return fail(error, error_size, "oversized table line");
        cursor = end ? end + 1 : cursor + len;
        const char *p = line_start;
        while (p < line_start+len && (*p == ' ' || *p == '\t')) p++;
        if (p >= line_start+len || *p != 'E' ||
            p+1 >= line_start+len || (* (p+1) != ' ' && *(p+1) != '\t'))
            continue;

        char line[8192];
        size_t n = (size_t)(line_start+len-(p+1));
        memcpy(line, p+1, n); line[n] = 0;
        int64_t v[5];
        if (integers(line, v, 5) != 5 || v[0] < 0 || v[1] < v[0] ||
            (have_event && v[0] < last_end) ||
            (v[2] != 0 && v[2] != 1) || v[3] < 0 || v[3] > 65535 ||
            (v[4] != 0 && v[4] != 1))
            return fail(error, error_size, "invalid/overlapping AV1 grain event");
        last_end = v[1]; have_event = true;
        /* FGS ShiftTable() inherits only models from active updated events.
         * A zero-duration update may still prepare the model for the next E. */
        if (v[2] == 1 && v[4] == 1) model = cursor;
        if (!found && ticks >= (uint64_t)v[0] && ticks < (uint64_t)v[1]) {
            if (v[2] != 1) return fail(error, error_size, "grain event disabled at selected time");
            if (!model) return fail(error, error_size, "grain model unavailable before update");
            selected_model = model;
            selected_seed = v[3];
            selected_start = v[0];
            found = true;
        }
    }
    if (!found) return fail(error, error_size, "no grain event at selected timeline time");

    /* Reuse the strict single-event validator for the selected model body. */
    const char *body_end = selected_model;
    while (*body_end) {
        const char *end = strchr(body_end, '\n');
        size_t len = end ? (size_t)(end - body_end) : strlen(body_end);
        const char *p = body_end;
        while (p < body_end+len && (*p == ' ' || *p == '\t')) p++;
        if (*p == 'E' && (p+1 < body_end+len) &&
            (*(p+1) == ' ' || *(p+1) == '\t')) break;
        body_end = end ? end+1 : body_end+len;
    }
    size_t body_len = (size_t)(body_end-selected_model);
    if (body_len > SIZE_MAX-128) return fail(error, error_size, "grain model too large");
    char *single = malloc(body_len+128);
    if (!single) return fail(error, error_size, "out of memory");
    int prefix = snprintf(single, 128, "filmgrn1\nE 0 9223372036854775807 1 %lld 1\n",
                          (long long)selected_seed);
    memcpy(single+prefix, selected_model, body_len);
    single[prefix+body_len] = 0;
    bool valid = fgs_parse_table(single, out, error, error_size);
    free(single);
    if (valid && event_start) *event_start = (uint64_t)selected_start;
    return valid;
}

bool fgs_parse_table_at(const char *text, uint64_t ticks, FgsGrainModel *out,
                        char *error, size_t error_size)
{
    return fgs_parse_table_at_event(text, ticks, out, NULL, error, error_size);
}

uint16_t fgs_seed_for_frame(uint16_t base_seed, uint64_t ticks,
                             uint64_t event_start, uint32_t fps_num, uint32_t fps_den)
{
    /* Each event's first matching video packet is advanced once, then every
     * subsequent packet increments its 16-bit seed by DEFAULT_GRAIN_SEED.
     * For CFR, packet ordinal is estimated from the nominal frame rate. */
    if (!fps_num || !fps_den || ticks < event_start) return base_seed;
    uint64_t denominator = UINT64_C(10000000) * fps_den;
    uint64_t first = (event_start * fps_num + denominator - 1) / denominator;
    uint64_t current = (ticks * fps_num + denominator - 1) / denominator;
    uint64_t frames = current >= first ? current - first + 1 : 1;
    return (uint16_t)(base_seed + (uint16_t)(frames * UINT64_C(10956)));
}

void fgs_scale_model(const FgsGrainModel *source, int percent, FgsGrainModel *dest)
{
    if (percent < 0) percent = 0;
    if (percent > 100) percent = 100;
    *dest = *source;
    for (int p=0; p<3; p++)
        for (int i=0; i<source->points_count[p]; i++)
            dest->points[p][i][1] =
                (uint8_t)((source->points[p][i][1] * percent + 50) / 100);
}
