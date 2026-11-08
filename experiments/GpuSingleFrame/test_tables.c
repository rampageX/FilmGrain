#include "fgs_table.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static char *read_text(const char *path)
{
    FILE *file = fopen(path, "rb");
    if (!file) return NULL;
    if (fseek(file, 0, SEEK_END)) {fclose(file); return NULL;}
    long size = ftell(file);
    if (size < 0 || size > 4000000 || fseek(file, 0, SEEK_SET)) {fclose(file); return NULL;}
    char *buffer = malloc((size_t)size + 1);
    if (!buffer) {fclose(file); return NULL;}
    if (fread(buffer, 1, (size_t)size, file) != (size_t)size) {free(buffer); fclose(file); return NULL;}
    buffer[size] = 0;
    fclose(file);
    return buffer;
}

int main(int argc, char **argv)
{
    char error[256];
    FgsGrainModel original, scaled;
    const char *bad[] = {
        "nottable\n",
        "filmgrn1\nE 0 12 1 1 1\nE 12 24 1 1 1\n",
        "filmgrn1\nE 0 12 1 1 0\n",
    };
    for (size_t i=0; i<sizeof(bad)/sizeof(bad[0]); i++) {
        if (fgs_parse_table(bad[i], &original, error, sizeof(error))) {
            fprintf(stderr, "Rejected fixture %zu unexpectedly accepted\n", i);
            return 1;
        }
    }
    if (argc == 1) {fprintf(stderr, "Specify at least one .tbl file\n"); return 2;}
    for (int arg=1; arg<argc; arg++) {
        char *table = read_text(argv[arg]);
        if (!table) {fprintf(stderr, "Cannot read %s\n", argv[arg]); return 2;}
        if (!fgs_parse_table(table, &original, error, sizeof(error))) {
            fprintf(stderr, "FAIL %s: %s\n", argv[arg], error);
            free(table); return 1;
        }
        fgs_scale_model(&original, 100, &scaled);
        if (memcmp(&original, &scaled, sizeof(original))) {
            fprintf(stderr, "100%% strength changed a model\n");
            free(table); return 1;
        }
        fgs_scale_model(&original, 40, &scaled);
        for (int p=0; p<3; p++) {
            for (int i=0; i<original.points_count[p]; i++) {
                if (scaled.points[p][i][0] != original.points[p][i][0] ||
                    scaled.points[p][i][1] != (original.points[p][i][1]*40+50)/100) {
                    fprintf(stderr, "Strength rounding mismatch\n");
                    free(table); return 1;
                }
            }
        }
        printf("PASS %s seed=%u luma=%d chroma=%d/%d\n", argv[arg],
               original.seed, original.points_count[0],
               original.points_count[1], original.points_count[2]);
        free(table);
    }
    return 0;
}
