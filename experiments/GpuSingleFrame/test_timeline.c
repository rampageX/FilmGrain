#include "fgs_table.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static char *load(const char *name)
{
    FILE *f = fopen(name, "rb");
    if (!f || fseek(f, 0, SEEK_END)) return NULL;
    long bytes = ftell(f);
    if (bytes < 1 || bytes > 4*1024*1024 || fseek(f, 0, SEEK_SET)) return NULL;
    char *data = malloc((size_t)bytes+1);
    if (!data) return NULL;
    if (fread(data, 1, (size_t)bytes, f) != (size_t)bytes) {
        free(data); fclose(f); return NULL;
    }
    fclose(f); data[bytes] = 0;
    return data;
}

static int verify(const char *path)
{
    char *data = load(path), error[256];
    FgsGrainModel model;
    if (!data) { fprintf(stderr, "Cannot read %s\n", path); return 1; }
    const uint64_t ticks[] = {0, 400000, 150000000, 9990000000ULL};
    for (size_t t = 0; t < sizeof(ticks)/sizeof(ticks[0]); t++) {
        if (!fgs_parse_table_at(data, ticks[t], &model, error, sizeof(error))) {
            fprintf(stderr, "FAIL %s at %llu ticks: %s\n", path,
                    (unsigned long long)ticks[t], error);
            free(data); return 1;
        }
    }
    /* R6: all 1% AV1 strength positions must preserve the model's seed and
     * apply FGS integer scaling without rounding to multiples of five. */
    FgsGrainModel scaled;
    for (int percent=1; percent<=100; percent++) {
        fgs_scale_model(&model, percent, &scaled);
        if (scaled.seed != model.seed) {
            fprintf(stderr, "Seed changed at %d%% in %s\n", percent, path);
            free(data); return 1;
        }
        for (int channel=0; channel<3; channel++) {
            for (int i=0; i<model.points_count[channel]; i++) {
                if (scaled.points[channel][i][0] != model.points[channel][i][0] ||
                    scaled.points[channel][i][1] !=
                        (model.points[channel][i][1]*percent+50)/100) {
                    fprintf(stderr, "1%% scaling mismatch at %d%% in %s\n", percent, path);
                    free(data); return 1;
                }
            }
        }
    }
    /* Verify every event, including its first and last valid 100-ns tick. */
    size_t events = 0;
    const char *cursor = data;
    while (*cursor) {
        if ((cursor == data || cursor[-1] == '\n') && cursor[0] == 'E' && cursor[1] == ' ') {
            unsigned long long start, end, seed;
            int enabled, update;
            if (sscanf(cursor, "E %llu %llu %d %llu %d",
                       &start, &end, &enabled, &seed, &update) != 5 || end < start)
                {fprintf(stderr, "Invalid fixture event: %s\n", path); free(data); return 1;}
            uint64_t stamps[2] = {(uint64_t)start, end > start ? (uint64_t)(end-1) : (uint64_t)start};
            for (int i=0; i<(end > start ? 2 : 0); i++) {
                bool success = fgs_parse_table_at(data, stamps[i], &model, error, sizeof(error));
                if (success != (enabled == 1) || (success && model.seed != (uint16_t)seed)) {
                    fprintf(stderr, "Event boundary failure %s @%llu: %s\n", path,
                            (unsigned long long)stamps[i], error);
                    free(data); return 1;
                }
            }
            events++;
        }
        const char *next = strchr(cursor, '\n');
        if (!next) break;
        cursor = next+1;
    }
    printf("PASS timeline %s (%zu events, 2 boundaries each)\n", path, events);
    free(data);
    return 0;
}

static int synthetic(void)
{
    /* First model remains active through update=0; disabled/missing times fail. */
    const char sample[] =
        "filmgrn1\n"
        "E 0 100 1 123 1\n"
        "p 0 6 0 8 0 1 128 192 256 128 192 256\n"
        "sY 1 1 8\n"
        "sCb 0\n"
        "sCr 0\n"
        "cY\n"
        "cCb\n"
        "cCr\n"
        "E 100 200 1 456 0\n"
        "E 200 300 0 789 0\n"
        "E 300 400 1 42 1\n"
        "p 0 6 0 8 0 1 128 192 256 128 192 256\n"
        "sY 1 1 99\n"
        "sCb 0\n"
        "sCr 0\n"
        "cY\n"
        "cCb\n"
        "cCr\n"
        "E 400 400 1 999 1\n"
        "p 0 6 0 8 0 1 128 192 256 128 192 256\n"
        "sY 1 1 77\n"
        "sCb 0\n"
        "sCr 0\n"
        "cY\n"
        "cCb\n"
        "cCr\n"
        "E 400 500 1 808 0\n";
    FgsGrainModel model;
    char error[256];
    uint64_t event_start = UINT64_MAX;
    if (!fgs_parse_table_at_event(sample, 150, &model, &event_start, error, sizeof(error)) ||
        model.seed != 456 || model.points[0][0][1] != 8 || event_start != 100) {
        fprintf(stderr, "update=0 inheritance failed: %s\n", error); return 1;
    }
    if (!fgs_parse_table_at_event(sample, 350, &model, &event_start, error, sizeof(error)) ||
        model.seed != 42 || model.points[0][0][1] != 99 || event_start != 300) {
        fprintf(stderr, "update=1 replacement failed: %s\n", error); return 1;
    }
    if (!fgs_parse_table_at_event(sample, 450, &model, &event_start, error, sizeof(error)) ||
        model.seed != 808 || model.points[0][0][1] != 77 || event_start != 400) {
        fprintf(stderr, "zero-duration model inheritance failed: %s\n", error); return 1;
    }
    if (fgs_parse_table_at(sample, 250, &model, error, sizeof(error)) ||
        fgs_parse_table_at(sample, 500, &model, error, sizeof(error)) ||
        fgs_parse_table(sample, &model, error, sizeof(error))) {
        fprintf(stderr, "negative event test unexpectedly accepted\n"); return 1;
    }
    puts("PASS multi-event update/reuse/disabled/out-of-range");
    return 0;
}

static int seed_timeline(void)
{
    const uint64_t second = UINT64_C(10000000);
    const uint16_t step = 10956;
    if (fgs_seed_for_frame(0,0,0,24,1) != step ||
        fgs_seed_for_frame(0,second,0,24,1) != (uint16_t)(25u*step) ||
        fgs_seed_for_frame(17,10*second,10*second,24,1) != (uint16_t)(17u+step) ||
        fgs_seed_for_frame(17,11*second,10*second,24,1) != (uint16_t)(17u+25u*step) ||
        fgs_seed_for_frame(0,second,0,24000,1001) != (uint16_t)(25u*step) ||
        fgs_seed_for_frame(0,second,0,0,1) != 0 ||
        fgs_seed_for_frame(123,0,second,24,1) != 123) {
        fprintf(stderr,"FAIL grav1synth frame seed sequence\n");
        return 1;
    }
    /* The reported MaxMid sample at 65.588 s falls in the event starting
     * at 5.48 s. Its luma LFSR must not remain stuck at seed=0. */
    uint16_t seed = fgs_seed_for_frame(0,UINT64_C(655880000),
                                        UINT64_C(54800000),24000,1001);
    if (!seed || seed == fgs_seed_for_frame(0,UINT64_C(655880000),
                                            UINT64_C(54800000),30,1)) {
        fprintf(stderr,"FAIL MaxMid seed did not advance or ignored FPS\n");
        return 1;
    }
    puts("PASS R8 wrapping seed updates, event reset, fractional FPS, MaxMid sample");
    return 0;
}

int main(int argc, char **argv)
{
    if (synthetic() || seed_timeline()) return 1;
    if (argc < 2) return 2;
    for (int n = 1; n < argc; n++) if (verify(argv[n])) return 1;
    return 0;
}
