#ifndef FGS_FILM_BORDER_FOCUS_H
#define FGS_FILM_BORDER_FOCUS_H

/* Independent implementation of the same normalized cover/zoom model used
 * by FilmBorderFramingCore for FFmpeg. No source code is imported from FilmFrame.
 * Focus is the desired center of the source rectangle in [0,1] coordinates.
 * Clamping the rectangle prevents any empty content at the picture gate.
 */
#include <math.h>
struct fgs_focus_rect {
    double left,top,width,height;
};
static inline double fgs_focus_clamp(double x,double a,double b)
{
    return fmax(a,fmin(b,x));
}
static inline struct fgs_focus_rect fgs_focus_crop(
    int width,int height,int aperture_width,int aperture_height,
    double focus_x,double focus_y,double zoom)
{
    const double scale=fmax((double)aperture_width/width,
                            (double)aperture_height/height)*zoom;
    struct fgs_focus_rect r;
    r.width=aperture_width/scale;
    r.height=aperture_height/scale;
    r.left=fgs_focus_clamp(focus_x*width-r.width/2.0,0,width-r.width);
    r.top=fgs_focus_clamp(focus_y*height-r.height/2.0,0,height-r.height);
    return r;
}
static inline void fgs_focus_zoom_at(
    int width,int height,int aperture_width,int aperture_height,
    double u,double v,double new_zoom,
    double *focus_x,double *focus_y,double *zoom)
{
    struct fgs_focus_rect before=fgs_focus_crop(width,height,aperture_width,aperture_height,
                                                 *focus_x,*focus_y,*zoom);
    const double source_x=before.left+u*before.width;
    const double source_y=before.top+v*before.height;
    struct fgs_focus_rect after=fgs_focus_crop(width,height,aperture_width,aperture_height,
                                                *focus_x,*focus_y,new_zoom);
    *focus_x=fgs_focus_clamp((source_x-u*after.width+after.width/2.0)/width,0,1);
    *focus_y=fgs_focus_clamp((source_y-v*after.height+after.height/2.0)/height,0,1);
    *zoom=new_zoom;
}
static inline void fgs_focus_pan(
    int width,int height,int aperture_width,int aperture_height,
    double delta_x,double delta_y,
    double *focus_x,double *focus_y,double zoom)
{
    struct fgs_focus_rect r=fgs_focus_crop(width,height,aperture_width,aperture_height,
                                             *focus_x,*focus_y,zoom);
    *focus_x=fgs_focus_clamp((r.left-delta_x*r.width/aperture_width+r.width/2.0)/width,0,1);
    *focus_y=fgs_focus_clamp((r.top-delta_y*r.height/aperture_height+r.height/2.0)/height,0,1);
}
#endif
