#ifndef FGS_FILM_BORDER_GEOMETRY_H
#define FGS_FILM_BORDER_GEOMETRY_H

#include <stdbool.h>
#include <math.h>

struct fgs_film_border_geometry {
    int aperture_width, aperture_height;
    int aperture_x, aperture_y;
    int shift_x, shift_y;
};

static inline double fgs_film_border_hash(double seconds, double rate,
                                       double multiplier, double seed, double scale)
{
    double value = fabs(sin(floor(seconds * rate) * multiplier + seed) * scale);
    return value - floor(value);
}

/* Match the requested pixel offsets in FilmBorderEncodingCore's S2 graph.
 * The compositor applies any chroma-plane alignment separately. */
static inline struct fgs_film_border_geometry
fgs_film_border_geometry(int width, int height, double seconds, bool dynamic)
{
    struct fgs_film_border_geometry result = {0};
    result.aperture_width = ((width * 1680 + 960) / 1920) & ~1;
    result.aperture_height = ((height * 990 + 540) / 1080) & ~1;
    result.aperture_x = (width - result.aperture_width) / 2;
    result.aperture_y = (height - result.aperture_height) / 2;
    if (dynamic) {
        double x = fgs_film_border_hash(seconds, 13, 12.9898, 23, 43758.5453);
        double y = fgs_film_border_hash(seconds, 13, 78.233, 97, 23577.731);
        if (x < 0.12) result.shift_x = x < 0.06 ? -2 : 2;
        if (y < 0.19) result.shift_y = y < 0.07 ? -3 : 2;
        if (fgs_film_border_hash(seconds, 29.97, 40.317, 31, 17231.213) < 0.13)
            result.shift_x++;
        if (fgs_film_border_hash(seconds, 29.97, 91.789, 17, 23577.731) < 0.17)
            result.shift_y++;
    }
    return result;
}

#endif
