#ifndef FGS_FILM_BORDER_GEOMETRY_H
#define FGS_FILM_BORDER_GEOMETRY_H
#define FGS_CLASSIC_APERTURE_HEIGHT_1080 748

#include <stdbool.h>
#include <math.h>
#include "film_border_motion.h"

struct fgs_film_border_geometry {
    int aperture_width, aperture_height;
    int aperture_x, aperture_y;
    double shift_x, shift_y;
};

/* Match the requested pixel offsets in FilmBorderEncodingCore's S2 graph.
 * The compositor applies any chroma-plane alignment separately. */
static inline struct fgs_film_border_geometry
fgs_film_border_geometry_adjusted(int width,int height,double seconds,bool dynamic,
    double amp,double speed,double frequency)
{
    struct fgs_film_border_geometry result = {0};
    result.aperture_width = ((width * 1680 + 960) / 1920) & ~1;
    result.aperture_height = ((height * 990 + 540) / 1080) & ~1;
    result.aperture_x = (width - result.aperture_width) / 2;
    result.aperture_y = (height - result.aperture_height) / 2;
    struct fgs_film_motion motion=fgs_film_border_motion_adjusted(seconds,dynamic,amp,speed,frequency);
    result.shift_x=motion.shift_x;
    result.shift_y=motion.shift_y;
    return result;
}
static inline struct fgs_film_border_geometry
fgs_film_border_geometry(int width,int height,double seconds,bool dynamic)
{
    return fgs_film_border_geometry_adjusted(width,height,seconds,dynamic,1,1,1);
}

#endif
