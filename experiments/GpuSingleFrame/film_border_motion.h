#ifndef FGS_FILM_BORDER_MOTION_H
#define FGS_FILM_BORDER_MOTION_H
#include <stdbool.h>
#include <math.h>

/* Independent projection gate weave motion model. Stable S4 profile v1:
 * frame-slot registration impulses, deterministic on PTS. Future motion
 * profiles belong here, not in border style/assets or output geometry. */
struct fgs_film_motion { double shift_x, shift_y; };
static inline double fgs_film_border_hash(double seconds, double rate,
                                       double multiplier, double seed, double scale)
{
    double value = fabs(sin(floor(seconds * rate) * multiplier + seed) * scale);
    return value - floor(value);
}

static inline struct fgs_film_motion fgs_film_border_motion_adjusted(
    double seconds,bool dynamic,double amp,double speed,double frequency)
{
    struct fgs_film_motion result={0};
    if (dynamic) {
        double x = fgs_film_border_hash(seconds, 13*speed, 12.9898, 23, 43758.5453);
        double y = fgs_film_border_hash(seconds, 13*speed, 78.233, 97, 23577.731);
        if (x < 0.12*frequency) result.shift_x = x < 0.06*frequency ? -2 : 2;
        if (y < 0.19*frequency) result.shift_y = y < 0.07*frequency ? -3 : 2;
        if (fgs_film_border_hash(seconds, 29.97*speed, 40.317, 31, 17231.213) < 0.13*frequency)
            result.shift_x++;
        if (fgs_film_border_hash(seconds, 29.97*speed, 91.789, 17, 23577.731) < 0.17*frequency)
            result.shift_y++;
    }
    result.shift_x*=amp;
    result.shift_y*=amp;
    return result;
}
static inline struct fgs_film_motion fgs_film_border_motion(double seconds,bool dynamic)
{
    return fgs_film_border_motion_adjusted(seconds,dynamic,1,1,1);
}
#endif
