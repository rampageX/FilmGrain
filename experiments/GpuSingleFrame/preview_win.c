/* FGS isolated GPU single-frame lab. No FGS code or encoding path is touched.
 * Win32 + libplacebo D3D11. Input: one pre-decoded, planar YUV420p frame.
 * Compile with MSYS2 UCRT64 / mingw-w64-ucrt-x86_64-libplacebo.
 */
#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0A00
#define COBJMACROS
#include <windows.h>
#include <dxgi1_6.h>
#include <psapi.h>

#include <libplacebo/log.h>
#include <libplacebo/d3d11.h>
#include <libplacebo/dispatch.h>
#include <libplacebo/renderer.h>
#include <libplacebo/shaders/custom.h>
#include <libplacebo/shaders/lut.h>
#include <libplacebo/utils/upload.h>

#include <math.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

#include "fgs_table.h"
#include "film_border_geometry.h"
#include "film_border_focus.h"

static HWND s_window, s_view, s_time_edit;
static WNDPROC s_original_edit_proc;
static pl_log s_log;
static pl_d3d11 s_device;
static pl_swapchain s_swapchain;
static pl_renderer s_renderer;
static pl_tex s_textures[3];
static pl_tex s_plate_textures[3];
static pl_tex s_border_texture, s_border_content, s_border_composite;
static pl_dispatch s_border_dispatch;
static const wchar_t *s_border_path;
static bool s_border_weave;
static bool s_border_settings;
static int s_border_mode; /* legacy OFF/STATIC/WEAVE fallback */
static const wchar_t *s_border_choices_file;
static const wchar_t *s_initial_border_mode;
static HWND s_border_list;
static WNDPROC s_original_border_list_proc;
static const char *s_border_action="APPLY";
#define FGS_BORDER_MAX_CHOICES 48
#define FGS_BORDER_LIST_ID 301
struct FgsBorderChoice {
    char mode[96];
    wchar_t title[128];
    wchar_t raw_path[32768];
    wchar_t thumbnail_path[32768];
    bool user;
    bool weave;
    int aperture_width,aperture_height; /* 1920x1080 coordinate system */
};
static struct FgsBorderChoice s_border_choices[FGS_BORDER_MAX_CHOICES];
static int s_border_choices_count, s_border_selection;
static int s_border_style_indices[FGS_BORDER_MAX_CHOICES],s_border_style_count;
static HBITMAP s_border_thumb_bitmaps[FGS_BORDER_MAX_CHOICES];
static int s_border_arrow_hover, s_border_arrow_pressed;
static int s_border_wheel_delta;
static bool s_border_arrow_tracking;
enum { BORDER_SIDEBAR_WIDTH=164, BORDER_THUMB_W=128, BORDER_THUMB_H=72,
       BORDER_LIST_ITEM_H=104, BORDER_LIST_TOP=54, BORDER_LIST_FOOT=53 };
static double s_motion_amp=1,s_motion_speed=1,s_motion_frequency=1;
static double s_focus_x=0.5,s_focus_y=0.5,s_focus_zoom=1.0;
static bool s_compose_mode=false,s_focus_dragging=false;
static bool s_compose_old_pixel_view;
static int s_compose_old_pan_x,s_compose_old_pan_y;
static int s_preview_aperture_width,s_preview_aperture_height;
static int s_focus_last_x,s_focus_last_y;
static HWND s_focus_x_edit,s_focus_y_edit;
static WNDPROC s_original_focus_edit_proc;
static struct pl_frame s_image;
static enum pl_color_transfer s_hdr_transfer;
static bool s_source_full, s_hdr_display;
static bool s_combined_grade, s_hint_en;
static const wchar_t *s_deinterlace = L"";
static FgsGrainModel s_original;
static int s_strength = 100, s_width, s_height;
static bool s_has_grain = true;
static bool s_plate_mode = false;
static const wchar_t *s_plate_path, *s_plate_frame_path;
static double s_plate_cycle_seconds;
static struct pl_hook s_plate_hook, s_pixel_hook;
static bool s_fgsim_mode, s_digital_mode;
static const wchar_t *s_fgsim_noise_path, *s_fgsim_shader_path, *s_digital_path, *s_noise_ffmpeg;
static char *s_fgsim_shader;
static pl_tex s_pixel_noise;
static const wchar_t *const s_fgsim_names[] = {L"Light",L"Normal",L"Medium",L"Heavy"};
static const wchar_t *s_result_path;
static struct pl_custom_lut *s_lut_source;
static struct pl_custom_lut s_lut_variants[3];
static float *s_lut_storage[3];
static struct pl_custom_lut s_lut_dynamic;
static float *s_lut_dynamic_data;
static int s_lut_dynamic_percent = -1;
static int s_lut_size[3];
static int s_lut_strength = 75;
static bool s_lut_enabled = false;
static int s_swap_width, s_swap_height;
static bool s_display_grain = true, s_first = true, s_benchmark = false;
static int s_drag_kind = 0;
static HBRUSH s_panel_brush;
static HBRUSH s_edit_brush;
static HFONT s_panel_font;
static int s_time_edit_width = 108, s_time_edit_height = 24;
static bool s_window_shown = false, s_restore_maximized = false;
static bool s_pixel_view = false, s_panning = false, s_frame_loading = false;
static int s_pan_x = 0, s_pan_y = 0, s_pan_last_x, s_pan_last_y;
static const wchar_t *s_video_path, *s_ffmpeg_path, *s_frame_path;
static char *s_table_data;
static int s_frame_depth;
static double s_frame_seconds, s_pending_seconds, s_duration_seconds;
static uint64_t s_grain_event_start;
static uint32_t s_fps_num = 30, s_fps_den = 1;
enum { PANEL_HEIGHT = 210, BORDER_PANEL_HEIGHT=410, BORDER_TIMELINE_SHIFT=64,
       MSG_SEEK_FRAME = WM_APP+7 };
static int border_timeline_shift(void){return s_border_settings && s_video_path ? BORDER_TIMELINE_SHIFT : 0;}
static int panel_height(void){return s_border_settings?BORDER_PANEL_HEIGHT+border_timeline_shift():PANEL_HEIGHT;}
static double s_setup_ms;
static LARGE_INTEGER s_frequency;

static double timer_ms(void)
{
    LARGE_INTEGER counter;
    QueryPerformanceCounter(&counter);
    return (double) counter.QuadPart * 1000.0 / (double) s_frequency.QuadPart;
}

static double process_cpu_ms(void)
{
    FILETIME create, exit_time, kernel, user;
    if (!GetProcessTimes(GetCurrentProcess(), &create, &exit_time, &kernel, &user)) return -1;
    ULARGE_INTEGER k, u;
    k.LowPart = kernel.dwLowDateTime; k.HighPart = kernel.dwHighDateTime;
    u.LowPart = user.dwLowDateTime; u.HighPart = user.dwHighDateTime;
    return (double)(k.QuadPart + u.QuadPart) / 10000.0;
}

static double process_memory_mb(void)
{
    PROCESS_MEMORY_COUNTERS info;
    if (!GetProcessMemoryInfo(GetCurrentProcess(), &info, sizeof(info))) return -1;
    return (double) info.WorkingSetSize / 1048576.0;
}

typedef struct FgsSavedWindowBounds {
    RECT normal;
    LONG maximized;
} FgsSavedWindowBounds;

static const wchar_t s_window_key[] = L"Software\\FilmGrainStudio\\GpuAdvancedPreview";
static const wchar_t s_border_window_key[] = L"Software\\FilmGrainStudio\\FilmBorderPreview";

/* Store only standalone window geometry, never an FGS encoding preference. */
static void save_window_bounds(HWND window)
{
    WINDOWPLACEMENT placement = {0};
    placement.length = sizeof(placement);
    if (!GetWindowPlacement(window,&placement)) return;
    FgsSavedWindowBounds bounds = {
        .normal = placement.rcNormalPosition,
        .maximized = placement.showCmd == SW_SHOWMAXIMIZED ||
                     (placement.flags & WPF_RESTORETOMAXIMIZED) != 0,
    };
    HKEY key;
    if (RegCreateKeyExW(HKEY_CURRENT_USER,s_border_settings?s_border_window_key:s_window_key,0,NULL,0,KEY_SET_VALUE,
                        NULL,&key,NULL) != ERROR_SUCCESS) return;
    RegSetValueExW(key,L"WindowBounds",0,REG_BINARY,(const BYTE *)&bounds,sizeof(bounds));
    RegCloseKey(key);
}

static void restore_window_bounds(HWND window)
{
    HKEY key;
    if (RegOpenKeyExW(HKEY_CURRENT_USER,s_border_settings?s_border_window_key:s_window_key,0,KEY_QUERY_VALUE,&key) != ERROR_SUCCESS)
        return;
    FgsSavedWindowBounds bounds;
    DWORD type = 0, bytes = sizeof(bounds);
    LONG result = RegQueryValueExW(key,L"WindowBounds",NULL,&type,(BYTE *)&bounds,&bytes);
    RegCloseKey(key);
    if (result != ERROR_SUCCESS || type != REG_BINARY || bytes != sizeof(bounds)) return;

    RECT rect = bounds.normal;
    int width = rect.right-rect.left, height = rect.bottom-rect.top;
    if (width < (s_border_settings?980:820) || width > 16384 ||
        height < (s_border_settings?720+border_timeline_shift():420) || height > 16384) return;
    /* Monitor removal must not resurrect the window outside all displays. */
    HMONITOR monitor = MonitorFromRect(&rect,MONITOR_DEFAULTTONULL);
    MONITORINFO info = {0};
    info.cbSize = sizeof(info);
    if (!monitor || !GetMonitorInfoW(monitor,&info)) return;
    RECT work = info.rcWork;
    if (width > work.right-work.left) width = work.right-work.left;
    if (height > work.bottom-work.top) height = work.bottom-work.top;
    if (width < (s_border_settings?980:820) ||
        height < (s_border_settings?720+border_timeline_shift():420)) return;
    int x = rect.left, y = rect.top;
    if (x < work.left) x = work.left;
    if (y < work.top) y = work.top;
    if (x > work.right-width) x = work.right-width;
    if (y > work.bottom-height) y = work.bottom-height;
    SetWindowPos(window,NULL,x,y,width,height,SWP_NOZORDER|SWP_NOACTIVATE);
    s_restore_maximized = bounds.maximized != 0;
}

/* Only an explicit Apply writes a result. Cancel/close never changes FGS. */
static bool write_result(void)
{
    if (!s_result_path) return true;
    size_t length = wcslen(s_result_path);
    wchar_t *temp = calloc(length + 6, sizeof(wchar_t));
    if (!temp) return false;
    wcscpy(temp, s_result_path);
    wcscat(temp, L".tmp");
    FILE *file = _wfopen(temp, L"wb");
    if (!file) { free(temp); return false; }
    int rc;
    if (s_border_settings) {
        const char *mode=s_border_list && s_border_choices_count>0 ?
            s_border_choices[s_border_selection].mode :
            s_border_mode==2?"DARK_WEAVE":s_border_mode==1?"DARK_STATIC":"OFF";
        rc = fprintf(file, "{\"Version\":5,\"Action\":\"%s\",\"FilmBorderMode\":\"%s\",\"MotionAmplitude\":%.3f,\"MotionSpeed\":%.3f,\"MotionFrequency\":%.3f,\"FocusX\":%.10g,\"FocusY\":%.10g,\"Zoom\":%.10g}\n",
            s_border_action,mode,s_motion_amp,s_motion_speed,s_motion_frequency,s_focus_x,s_focus_y,s_focus_zoom);
    } else if (s_fgsim_mode || s_digital_mode) {
        rc = fprintf(file,
            "{\"Version\":3,\"PixelMode\":\"%s\",\"%s\":%d,"
            "\"GrainEnabled\":%s,\"LutStrength\":%d,\"LutEnabled\":%s}\n",
            s_fgsim_mode?"FGSIM":"DIGITAL", s_fgsim_mode?"FgsimPreset":"GrainStrength",
            s_strength, s_display_grain?"true":"false", s_lut_strength,
            s_lut_source && s_lut_enabled && s_lut_strength?"true":"false");
    } else if (s_plate_mode) {
        rc = fprintf(file,
            "{\"Version\":2,\"PixelMode\":\"PLATE\",\"PlateStrength\":%d,"
            "\"GrainEnabled\":%s,\"LutStrength\":%d,\"LutEnabled\":%s}\n",
            s_strength, s_display_grain && s_strength ? "true" : "false",
            s_lut_strength, s_lut_source && s_lut_enabled && s_lut_strength ? "true" : "false");
    } else {
        rc = fprintf(file,
            "{\"Version\":1,\"GrainStrength\":%d,\"GrainEnabled\":%s,"
            "\"LutStrength\":%d,\"LutEnabled\":%s}\n",
            s_strength, s_has_grain && s_display_grain && s_strength ? "true" : "false",
            s_lut_strength, s_lut_source && s_lut_enabled && s_lut_strength ? "true" : "false");
    }
    int flushed = fflush(file);
    int closed = fclose(file);
    if (flushed || closed || rc < 0) {
        DeleteFileW(temp); free(temp); return false;
    }
    bool okay = MoveFileExW(temp, s_result_path,
                            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != 0;
    if (!okay) DeleteFileW(temp);
    free(temp);
    return okay;
}

static bool read_file(const wchar_t *path, size_t exact_bytes, size_t limit,
                      unsigned char **data, size_t *length)
{
    FILE *file = _wfopen(path, L"rb");
    if (!file) { fwprintf(stderr, L"Failed to open: %ls\n", path); return false; }
    if (_fseeki64(file, 0, SEEK_END)) { fclose(file); return false; }
    __int64 sz = _ftelli64(file);
    if (sz < 0 || (uint64_t)sz > limit || (exact_bytes && (uint64_t)sz != exact_bytes)) {
        fwprintf(stderr, L"Unexpected file size: %ls (%lld bytes)\n", path, (long long)sz);
        fclose(file); return false;
    }
    if (_fseeki64(file, 0, SEEK_SET)) { fclose(file); return false; }
    unsigned char *buf = malloc((size_t)sz + 1);
    if (!buf) { fclose(file); return false; }
    if (fread(buf, 1, (size_t)sz, file) != (size_t)sz) {
        free(buf); fclose(file); return false;
    }
    fclose(file);
    buf[sz] = 0;
    *data = buf;
    if (length) *length = (size_t)sz;
    return true;
}

static bool upload_border_texture(void);
static void redraw_controls(void);
/* A UTF-16LE catalog is written by the existing FGS managed importer. Each
 * line contains a stable ASCII mode ID, localized title and validated RGBA path.
 * Native UI never invents paths or mutates user files. */
static bool load_border_choices(const wchar_t *path, const wchar_t *initial_mode)
{
    unsigned char *bytes=NULL;
    size_t length=0;
    if(!read_file(path,0,65534,&bytes,&length))return false;
    if(length<4 || (length&1) || bytes[0]!=0xff || bytes[1]!=0xfe) {free(bytes);return false;}
    wchar_t *buffer=calloc(length/2+1,sizeof(wchar_t));
    if(!buffer){free(bytes);return false;}
    memcpy(buffer,bytes+2,length-2);
    free(bytes);
    wchar_t *line=buffer;
    for(wchar_t *p=buffer; ; p++) {
        if(*p==L'\r' || *p==L'\n' || !*p) {
            bool done=!*p;
            *p=0;
            if(*line && s_border_choices_count<FGS_BORDER_MAX_CHOICES) {
                wchar_t *first=wcschr(line,L'\t');
                if(first) {
                    *first++=0;
                    wchar_t *second=wcschr(first,L'\t');
                    if(second) {
                        *second++=0;
                        struct FgsBorderChoice *c=&s_border_choices[s_border_choices_count];
                        // Catalog v1.1 appends centered aperture geometry:
                        // mode<TAB>title<TAB>texture<TAB>width<TAB>height.
                        wchar_t *third=wcschr(second,L'\t'),*fourth=NULL,*fifth=NULL;
                        int apertureW=1680,apertureH=wcsncmp(line,L"DARK_",5)==0?990:748;
                        bool valid=third!=NULL;
                        if(valid) {
                            *third++=0;fourth=wcschr(third,L'\t');
                            valid=fourth!=NULL;
                        }
                        if(valid) {
                            *fourth++=0;
                            // v1.2 thumbnail path is an optional sixth column.
                            // Split before parsing numeric height (older five-column files still work).
                            fifth=wcschr(fourth,L'\t');
                            if(fifth)*fifth++=0;
                            wchar_t *endW=NULL,*endH=NULL;
                            long aw=wcstol(third,&endW,10),ah=wcstol(fourth,&endH,10);
                            valid=endW && !*endW && endH && !*endH &&
                                aw>=2 && aw<=1920 && ah>=2 && ah<=1080;
                            if(valid){apertureW=(int)aw;apertureH=(int)ah;}
                        }
                        valid=valid && wcslen(line)>0 && wcslen(line)<sizeof(c->mode) &&
                            wcslen(first)<sizeof(c->title)/sizeof(wchar_t) &&
                            wcslen(second)<sizeof(c->raw_path)/sizeof(wchar_t) &&
                            (!fifth || wcslen(fifth)<sizeof(c->thumbnail_path)/sizeof(wchar_t));
                        if(valid)for(const wchar_t *m=line;*m;m++)if(!((*m>=L'A'&&*m<=L'Z')||
                            (*m>=L'0'&&*m<=L'9')||*m==L'_'))valid=false;
                        if(valid) {
                            for(size_t j=0;j<wcslen(line);j++)c->mode[j]=(char)line[j];
                            wcscpy(c->title,first);
                            wcscpy(c->raw_path,second);
                            if(fifth)wcscpy(c->thumbnail_path,fifth);
                            else c->thumbnail_path[0]=0;
                            c->weave=wcsstr(line,L"_WEAVE")!=NULL;
                            c->user=wcsncmp(line,L"USER_",5)==0;
                            c->aperture_width=apertureW;c->aperture_height=apertureH;
                            if(initial_mode && !wcscmp(initial_mode,line))s_border_selection=s_border_choices_count;
                            s_border_choices_count++;
                        }
                    }
                }
            }
            if(done)break;
            while(p[1]==L'\r' || p[1]==L'\n')p++;
            line=p+1;
        }
    }
    free(buffer);
    if(s_border_choices_count<3 || strcmp(s_border_choices[0].mode,"OFF"))return false;
    // Each style has a STATIC and WEAVE catalog entry; render only STATIC here.
    for(int i=1;i<s_border_choices_count;i++) {
        const char *mode=s_border_choices[i].mode;
        size_t n=strlen(mode);
        if(n>7 && !strcmp(mode+n-7,"_STATIC"))
            s_border_style_indices[s_border_style_count++]=i;
    }
    return s_border_style_count>0;
}

static void border_choice_buttons(const RECT *client,RECT *import,RECT *remove,
                                  RECT *edit,RECT *rebuild)
{
    int top=client->bottom-panel_height()+(s_video_path?61:21)+border_timeline_shift();
    *rebuild=(RECT){client->right-508,top,client->right-390,top+29};
    *import=(RECT){client->right-376,top,client->right-278,top+29};
    *remove=(RECT){client->right-268,top,client->right-170,top+29};
    *edit=(RECT){client->right-160,top,client->right-13,top+29};
}
static void border_mode_buttons(const RECT *client,RECT *static_btn,RECT *weave_btn,RECT *off_btn)
{
    int top=client->bottom-panel_height()+(s_video_path?61:21)+border_timeline_shift();
    *static_btn=(RECT){144,top,230,top+29};
    *weave_btn=(RECT){238,top,324,top+29};
    *off_btn=(RECT){332,top,402,top+29};
}
static void border_sidebar_buttons(const RECT *client,RECT *up,RECT *down)
{
    int view_height=client->bottom-panel_height();
    *up=(RECT){12,12,BORDER_SIDEBAR_WIDTH-12,46};
    *down=(RECT){12,view_height-45,BORDER_SIDEBAR_WIDTH-12,view_height-11};
}
static int border_sidebar_hit(const RECT *client,POINT pos)
{
    RECT up,down;border_sidebar_buttons(client,&up,&down);
    return PtInRect(&up,pos)?-1:PtInRect(&down,pos)?1:0;
}
static void border_sidebar_redraw(void)
{
    if(!s_window)return;
    RECT client;GetClientRect(s_window,&client);
    RECT up,down;border_sidebar_buttons(&client,&up,&down);
    InvalidateRect(s_window,&up,FALSE);
    InvalidateRect(s_window,&down,FALSE);
}
/* Same window-proc subclassing pattern as the original dark combo: preserve
 * Escape/Enter in the list without inventing a new control class. */
static void border_list_scroll_lines(int lines);
static LRESULT CALLBACK border_list_proc(HWND list,UINT message,WPARAM wparam,LPARAM lparam)
{
    if(message==WM_MOUSEWHEEL) {
        // Scroll even though WS_VSCROLL is intentionally absent.
        s_border_wheel_delta+=(short)GET_WHEEL_DELTA_WPARAM(wparam);
        while(s_border_wheel_delta>=WHEEL_DELTA) {
            border_list_scroll_lines(-3);
            s_border_wheel_delta-=WHEEL_DELTA;
        }
        while(s_border_wheel_delta<=-WHEEL_DELTA) {
            border_list_scroll_lines(3);
            s_border_wheel_delta+=WHEEL_DELTA;
        }
        return 0;
    }
    if(message==WM_KEYDOWN && (wparam==VK_ESCAPE || wparam==VK_RETURN)) {
        SetFocus(s_window);
        return SendMessageW(s_window,message,wparam,lparam);
    }
    return CallWindowProcW(s_original_border_list_proc,list,message,wparam,lparam);
}
static void border_list_layout(const RECT *client)
{
    if(!s_border_list)return;
    int view_height=client->bottom-panel_height();
    int h=view_height-BORDER_LIST_TOP-BORDER_LIST_FOOT;
    if(h<1)h=1;
    MoveWindow(s_border_list,8,BORDER_LIST_TOP,BORDER_SIDEBAR_WIDTH-16,h,TRUE);
}
static int border_style_to_choice(int style,bool weave)
{
    if(style<0 || style>=s_border_style_count)return 0;
    int index=s_border_style_indices[style];
    if(!weave)return index;
    const char *base=s_border_choices[index].mode;
    size_t n=strlen(base);
    if(n<8)return index;
    for(int i=1;i<s_border_choices_count;i++) {
        const char *candidate=s_border_choices[i].mode;
        if(strlen(candidate)==n-7+6 && !strncmp(base,candidate,n-7) &&
           !strcmp(candidate+n-7,"_WEAVE"))return i;
    }
    return index;
}
static int border_choice_to_style(int choice)
{
    if(choice==0)return -1;
    for(int i=0;i<s_border_style_count;i++)
        if(choice==border_style_to_choice(i,false) || choice==border_style_to_choice(i,true))
            return i;
    return -1;
}
static void border_list_scroll_lines(int lines)
{
    if(!s_border_list || s_border_style_count<=0)return;
    int top=(int)SendMessageW(s_border_list,LB_GETTOPINDEX,0,0);
    if(top==LB_ERR)return;
    int target=top+lines;
    if(target<0)target=0;
    if(target>=s_border_style_count)target=s_border_style_count-1;
    SendMessageW(s_border_list,LB_SETTOPINDEX,target,0);
}
static void border_list_scroll(int direction)
{
    if(!s_border_list)return;
    RECT rc;GetClientRect(s_border_list,&rc);
    int visible=(rc.bottom-rc.top)/BORDER_LIST_ITEM_H;
    int shift=visible>1?visible-1:1;
    border_list_scroll_lines(direction<0?-shift:shift);
}
static void paint_border_sidebar(HDC dc,const RECT *client)
{
    if(!s_border_settings || !s_border_list)return;
    RECT sidebar={0,0,BORDER_SIDEBAR_WIDTH,client->bottom-panel_height()};
    HBRUSH bg=CreateSolidBrush(RGB(37,38,40));
    FillRect(dc,&sidebar,bg);DeleteObject(bg);
    RECT edge={BORDER_SIDEBAR_WIDTH-1,0,BORDER_SIDEBAR_WIDTH,sidebar.bottom};
    HBRUSH border=CreateSolidBrush(RGB(64,66,71));FillRect(dc,&edge,border);
    RECT up,down;border_sidebar_buttons(client,&up,&down);
    HBRUSH normal=CreateSolidBrush(RGB(27,28,30));
    HBRUSH hover=CreateSolidBrush(RGB(34,49,44));
    HBRUSH pressed=CreateSolidBrush(RGB(0,101,79));
    const RECT buttons[]={up,down};
    for(int i=0;i<2;i++) {
        int direction=i==0?-1:1;
        bool active=s_border_arrow_hover==direction;
        bool down_now=active && s_border_arrow_pressed==direction;
        FillRect(dc,&buttons[i],down_now?pressed:active?hover:normal);
        FrameRect(dc,&buttons[i],border);
        // DPI-neutral GDI triangle instead of tiny text labels and a new control.
        int cx=(buttons[i].left+buttons[i].right)/2;
        int cy=(buttons[i].top+buttons[i].bottom)/2;
        int half=(buttons[i].bottom-buttons[i].top)/4;
        POINT tri[3];
        if(direction<0) {
            tri[0]=(POINT){cx,cy-half};
            tri[1]=(POINT){cx-half,cy+half};
            tri[2]=(POINT){cx+half,cy+half};
        } else {
            tri[0]=(POINT){cx-half,cy-half};
            tri[1]=(POINT){cx+half,cy-half};
            tri[2]=(POINT){cx,cy+half};
        }
        COLORREF color=down_now?RGB(190,255,225):active?RGB(0,204,151):RGB(235,237,240);
        HBRUSH glyph=CreateSolidBrush(color);
        HPEN pen=CreatePen(PS_SOLID,1,color);
        HGDIOBJ oldbrush=SelectObject(dc,glyph),oldpen=SelectObject(dc,pen);
        Polygon(dc,tri,3);
        SelectObject(dc,oldpen);SelectObject(dc,oldbrush);
        DeleteObject(pen);DeleteObject(glyph);
    }
    DeleteObject(normal);DeleteObject(hover);DeleteObject(pressed);DeleteObject(border);
}
/* Border names are catalog entries, not a fixed three-state enum.
 * The catalog validates the actual style after arguments are parsed. */
static bool valid_border_mode_arg(const wchar_t *name)
{
    if(!wcscmp(name,L"OFF"))return true;
    size_t count=wcslen(name);
    if(count<8 || count>=96 || (wcscmp(name+count-7,L"_STATIC") &&
       wcscmp(name+count-6,L"_WEAVE")))return false;
    for(size_t i=0;i<count;i++)
        if(!((name[i]>=L'A' && name[i]<=L'Z') ||
             (name[i]>=L'0' && name[i]<=L'9') || name[i]==L'_'))return false;
    return name[0]!=L'_';
}

static bool border_choice_apply(int index,bool reload)
{
    if(index<0 || index>=s_border_choices_count)return false;
    struct FgsBorderChoice *c=&s_border_choices[index];
    const wchar_t *old=s_border_path;
    int old_index=s_border_selection;
    bool old_weave=s_border_weave;
    s_border_path=c->raw_path;
    s_border_weave=c->weave;
    s_border_selection=index;
    if(reload && old && wcscmp(old,s_border_path)) {
        if(s_device && s_device->gpu)pl_tex_destroy(s_device->gpu,&s_border_texture);
        if(!upload_border_texture()) {
            s_border_selection=old_index;s_border_weave=old_weave;s_border_path=old;
            if(s_device && s_device->gpu)upload_border_texture();
            return false;
        }
    }
    redraw_controls();
    if(s_border_list) {
        int style=border_choice_to_style(s_border_selection);
        SendMessageW(s_border_list,LB_SETCURSEL,style<0?(WPARAM)-1:(WPARAM)style,0);
        InvalidateRect(s_border_list,NULL,FALSE);
    }
    return true;
}
static bool border_mode_apply(bool weave)
{
    if(!s_border_selection) {s_border_weave=weave;redraw_controls();return true;}
    int style=border_choice_to_style(s_border_selection);
    if(style<0)return false;
    return border_choice_apply(border_style_to_choice(style,weave),false);
}

/* Original FGS 25/50/75/100% steps stay pre-baked. R4 adds a single reusable
 * dynamic LUT for 1% slider positions, without replacing the YUV GPU textures.
 * libplacebo may upload a new *LUT* texture when its signature changes. */
static bool load_lut(const wchar_t *path)
{
    unsigned char *text = NULL;
    size_t length;
    if (!read_file(path, 0, 32*1024*1024, &text, &length)) return false;
    /* Reject unsupported 1D/compound/huge LUTs BEFORE libplacebo allocates
     * their texture table. This is a laboratory cap, not an FGS limitation. */
    int size = 0;
    for (const char *line = (const char *)text; *line;) {
        while (*line == ' ' || *line == '\t') line++;
        if (!strncmp(line, "LUT_1D_SIZE", 11) &&
            (line[11] == ' ' || line[11] == '\t')) {
            fprintf(stderr, "1D and compound LUTs are not supported in this lab\n");
            free(text); return false;
        }
        if (!strncmp(line, "LUT_3D_SIZE", 11) &&
            (line[11] == ' ' || line[11] == '\t')) {
            int detected;
            if (size || sscanf(line+11, "%d", &detected) != 1 || detected < 2 || detected > 65) {
                fprintf(stderr, "Invalid 3D LUT size (supported: 2..65)\n");
                free(text); return false;
            }
            size = detected;
        }
        const char *end = strchr(line, '\n');
        if (!end) break;
        line = end+1;
    }
    if (!size) {
        fprintf(stderr, "Missing LUT_3D_SIZE in CUBE file\n");
        free(text); return false;
    }
    s_lut_source = pl_lut_parse_cube(s_log, (const char *)text, length);
    free(text);
    if (!s_lut_source) {
        fprintf(stderr, "LUT .cube parsing failed\n");
        return false;
    }
    int r = s_lut_source->size[0], g = s_lut_source->size[1];
    int b = s_lut_source->size[2];
    if (r < 2 || g < 2 || b < 2 || r > 65 || g > 65 || b > 65) {
        fprintf(stderr, "Only standalone 3D .cube LUT sizes 2..65 are supported\n");
        return false;
    }
    s_lut_size[0] = r; s_lut_size[1] = g; s_lut_size[2] = b;
    size_t count = (size_t)r*g*b;
    for (int step=1; step<=3; step++) {
        float *data = malloc(count*3*sizeof(float));
        if (!data) return false;
        s_lut_storage[step-1] = data;
        float amount = step/4.0f;
        for (int z=0; z<b; z++) for (int y=0; y<g; y++) for (int x=0; x<r; x++) {
            size_t i = ((size_t)(z*g+y)*r+x)*3;
            float identity[3] = { (float)x/(r-1), (float)y/(g-1), (float)z/(b-1) };
            for (int ch=0; ch<3; ch++)
                data[i+ch] = s_lut_source->data[i+ch]*amount + identity[ch]*(1.0f-amount);
        }
        s_lut_variants[step-1] = *s_lut_source;
        s_lut_variants[step-1].data = data;
        s_lut_variants[step-1].signature ^= UINT64_C(0x9e3779b97f4a7c15)*(uint64_t)step;
    }
    s_lut_dynamic_data = malloc(count*3*sizeof(float));
    if (!s_lut_dynamic_data) return false;
    s_lut_dynamic = *s_lut_source;
    s_lut_dynamic.data = s_lut_dynamic_data;
    s_lut_enabled = true;
    return true;
}

static void prepare_dynamic_lut(int percent)
{
    if (s_lut_dynamic_percent == percent) return;
    int r = s_lut_size[0], g = s_lut_size[1], b = s_lut_size[2];
    float amount = percent / 100.0f;
    for (int z=0; z<b; z++) for (int y=0; y<g; y++) for (int x=0; x<r; x++) {
        size_t i = ((size_t)(z*g+y)*r+x)*3;
        float identity[3] = { (float)x/(r-1), (float)y/(g-1), (float)z/(b-1) };
        for (int ch=0; ch<3; ch++)
            s_lut_dynamic_data[i+ch] =
                s_lut_source->data[i+ch]*amount + identity[ch]*(1.0f-amount);
    }
    /* The shader cache must see a distinct signature when data is replaced. */
    s_lut_dynamic.signature = s_lut_source->signature ^
                              (UINT64_C(0xd1b54a32d192ed03) + (uint64_t)percent);
    s_lut_dynamic_percent = percent;
}

static const struct pl_custom_lut *selected_lut(void)
{
    if (!s_lut_source || !s_lut_enabled || !s_lut_strength) return NULL;
    if (s_lut_strength == 100) return s_lut_source;
    if (!(s_lut_strength%25)) return &s_lut_variants[s_lut_strength/25-1];
    prepare_dynamic_lut(s_lut_strength);
    return &s_lut_dynamic;
}

static void set_grain(int strength)
{
    struct pl_film_grain_data *fg = &s_image.film_grain;
    memset(fg, 0, sizeof(*fg));
    if (s_plate_mode || s_fgsim_mode || s_digital_mode) return;
    if (!s_has_grain || !s_display_grain || strength == 0) return;
    FgsGrainModel model;
    fgs_scale_model(&s_original, strength, &model);
    fg->type = PL_FILM_GRAIN_AV1;
    /* Raw filmgrn1 presets often use seed=0. grav1synth increments that seed
     * per video packet before applying AV1 grain; using 0 directly would lock
     * libplacebo's luma LFSR to a constant instead of random noise. */
    fg->seed = fgs_seed_for_frame(model.seed,
        (uint64_t)llround(s_frame_seconds*10000000.0), s_grain_event_start,
        s_fps_num, s_fps_den);
    struct pl_av1_grain_data *p = &fg->params.av1;
    p->ar_coeff_lag = model.lag;
    p->ar_coeff_shift = model.ar_shift;
    p->grain_scale_shift = model.grain_shift;
    p->scaling_shift = model.scaling_shift;
    p->chroma_scaling_from_luma = model.chroma_from_luma != 0;
    p->overlap = model.overlap != 0;
    p->num_points_y = model.points_count[0];
    memcpy(p->points_y, model.points[0], sizeof(p->points_y));
    memcpy(p->ar_coeffs_y, model.coefficients[0], sizeof(p->ar_coeffs_y));
    for (int ch=0; ch<2; ch++) {
        p->num_points_uv[ch] = model.points_count[ch+1];
        for (int i=0; i<p->num_points_uv[ch]; i++) {
            p->points_uv[ch][i][0] = model.points[ch+1][i][0];
            p->points_uv[ch][i][1] = model.points[ch+1][i][1];
        }
        memcpy(p->ar_coeffs_uv[ch], model.coefficients[ch+1], sizeof(p->ar_coeffs_uv[ch]));
        p->uv_mult[ch] = (int8_t)(model.uv_mult[ch] > 127 ? model.uv_mult[ch]-256 : model.uv_mult[ch]);
        p->uv_mult_luma[ch] = (int8_t)(model.uv_luma_mult[ch] > 127 ? model.uv_luma_mult[ch]-256 : model.uv_luma_mult[ch]);
        p->uv_offset[ch] = (int16_t)model.uv_offset[ch];
    }
}

static bool upload_frame(const unsigned char *raw, int depth)
{
    const size_t bytes_per_sample = depth == 8 ? 1 : 2;
    const int widths[3] = {s_width, s_width/2, s_width/2};
    const int heights[3] = {s_height, s_height/2, s_height/2};
    size_t offset = 0;
    for (int i=0; i<3; i++) {
        struct pl_plane_data plane = {
            .type = PL_FMT_UNORM,
            .width = widths[i], .height = heights[i],
            .pixel_stride = bytes_per_sample,
            .row_stride = (size_t)widths[i] * bytes_per_sample,
            .pixels = raw + offset,
        };
        int sizes[4] = {depth == 8 ? 8 : 16, 0, 0, 0};
        int shifts[4] = {0, 0, 0, 0};
        pl_plane_data_from_comps(&plane, sizes, shifts);
        if (!pl_upload_plane(s_device->gpu, &s_image.planes[i], &s_textures[i], &plane)) {
            fprintf(stderr, "GPU upload failed for plane %d\n", i);
            return false;
        }
        s_image.planes[i].components = 1;
        s_image.planes[i].component_mapping[0] = i;
        s_image.planes[i].shift_x = s_image.planes[i].shift_y = 0;
        offset += (size_t)widths[i] * heights[i] * bytes_per_sample;
    }
    s_image.num_planes = 3;
    s_image.repr = (struct pl_color_repr) {
        .sys = s_hdr_transfer ? PL_COLOR_SYSTEM_BT_2020_NC : PL_COLOR_SYSTEM_BT_709,
        .levels = s_source_full ? PL_COLOR_LEVELS_FULL : PL_COLOR_LEVELS_LIMITED,
        .bits = { .sample_depth = depth == 8 ? 8 : 16,
                  .color_depth = depth, .bit_shift = 0 },
    };
    s_image.color = (struct pl_color_space) {
        .primaries = PL_COLOR_PRIM_BT_709, .transfer = PL_COLOR_TRC_BT_1886,
    };
    if (s_hdr_transfer)
        s_image.color = s_hdr_transfer == PL_COLOR_TRC_PQ ? pl_color_space_hdr10 : pl_color_space_bt2020_hlg;
    s_image.crop = (pl_rect2df) {0, 0, s_width, s_height};
    pl_gpu_finish(s_device->gpu);
    return true;
}

/* Plate planes live on the GPU alongside the source frame. FFmpeg is only
 * involved on the initial load and explicit seeks, never on slider updates.
 * Both sources arrive as right-justified 10-bit YUV420 (one R16 texture per
 * plane); this is the same pre-normalization space as PL_HOOK_NATIVE. */
static bool upload_plate(const unsigned char *raw)
{
    const int widths[3] = {s_width, s_width/2, s_width/2};
    const int heights[3] = {s_height, s_height/2, s_height/2};
    size_t offset = 0;
    for (int i=0; i<3; i++) {
        struct pl_plane_data plane = {
            .type = PL_FMT_UNORM,
            .width = widths[i], .height = heights[i],
            .pixel_stride = 2, .row_stride = (size_t)widths[i]*2,
            .pixels = raw+offset,
        };
        int sizes[4] = {16,0,0,0};
        int shifts[4] = {0,0,0,0};
        struct pl_plane uploaded = {0};
        pl_plane_data_from_comps(&plane,sizes,shifts);
        if (!pl_upload_plane(s_device->gpu,&uploaded,&s_plate_textures[i],&plane)) {
            fprintf(stderr,"Plate GPU upload failed for plane %d\n",i);
            return false;
        }
        offset += (size_t)widths[i]*heights[i]*2;
    }
    pl_gpu_finish(s_device->gpu);
    return true;
}

/* FFmpeg blend_vulkan overlay: source video is TOP, plate is BOTTOM. Apply
 * the Overlay operation independently to Y/U/V before BT.709 conversion.
 * libplacebo has already expanded the source's right-justified 10-bit samples
 * to 10-bit signal range at PL_HOOK_NATIVE. The separate plate textures still
 * need the 64x expansion because they are sampled directly as R16 UNORM. */
static float s_plate_origin_x = 0.0f;
static float s_plate_origin_y = 0.0f;
static struct pl_hook_res plate_overlay_hook(void *priv, const struct pl_hook_params *params)
{
    (void)priv;
    const struct pl_shader_desc samplers[3] = {
        {.desc={.name="fgsPlateY",.type=PL_DESC_SAMPLED_TEX},
         .binding={.object=s_plate_textures[0],.address_mode=PL_TEX_ADDRESS_CLAMP,.sample_mode=PL_TEX_SAMPLE_LINEAR}},
        {.desc={.name="fgsPlateU",.type=PL_DESC_SAMPLED_TEX},
         .binding={.object=s_plate_textures[1],.address_mode=PL_TEX_ADDRESS_CLAMP,.sample_mode=PL_TEX_SAMPLE_LINEAR}},
        {.desc={.name="fgsPlateV",.type=PL_DESC_SAMPLED_TEX},
         .binding={.object=s_plate_textures[2],.address_mode=PL_TEX_ADDRESS_CLAMP,.sample_mode=PL_TEX_SAMPLE_LINEAR}},
    };
    float opacity = s_display_grain ? (float)s_strength/100.0f : 0.0f;
    const float dimensions[2] = {(float)s_width,(float)s_height};
    /* The 1:1 viewer renders a crop, not the whole frame. Sample the plate
     * at the original full-frame coordinates to prevent pan misalignment. */
    const float offset[2] = {s_plate_origin_x,s_plate_origin_y};
    const struct pl_shader_var uniforms[3] = {
        {.var=pl_var_float("fgsPlateOpacity"),.data=&opacity,.dynamic=true},
        {.var=pl_var_vec2("fgsPlateDimensions"),.data=dimensions},
        {.var=pl_var_vec2("fgsPlateOffset"),.data=offset,.dynamic=true},
    };
    const char shader[] =
        "vec2 fgsUV = clamp((gl_FragCoord.xy + fgsPlateOffset) / fgsPlateDimensions, vec2(0.0), vec2(1.0));\n"
        "vec3 fgsTop = color.rgb;\n"
        "vec3 fgsBottom = vec3(texture(fgsPlateY,fgsUV).r,"
        " texture(fgsPlateU,fgsUV).r,texture(fgsPlateV,fgsUV).r) * 64.0;\n"
        "vec3 fgsOverlay = mix(2.0*fgsTop*fgsBottom,"
        " 1.0-2.0*(1.0-fgsTop)*(1.0-fgsBottom),step(vec3(0.5),fgsTop));\n"
        "color.rgb = mix(fgsTop,fgsOverlay,fgsPlateOpacity);\n";
    bool okay = pl_shader_custom(params->sh,&(struct pl_custom_shader){
        .description="FGS Grain Plate Overlay (GPU)",
        .input=PL_SHADER_SIG_COLOR,.output=PL_SHADER_SIG_COLOR,
        .body=shader,.descriptors=samplers,.num_descriptors=3,
        .variables=uniforms,.num_variables=3,
    });
    if (!okay) return (struct pl_hook_res){.failed=true};
    return (struct pl_hook_res){
        .output=PL_HOOK_SIG_COLOR,.sh=params->sh,
        .repr=params->repr,.color=params->color,
        .components=params->components,.rect=params->rect,
    };
}

static struct pl_hook_res pixel_grain_hook(void *priv, const struct pl_hook_params *params)
{
    (void)priv;
    const struct pl_shader_desc texture = {
        .desc={.name=s_fgsim_mode?"FGS_NOISE":"fgsNoise",.type=PL_DESC_SAMPLED_TEX},
        .binding={.object=s_pixel_noise,.address_mode=PL_TEX_ADDRESS_CLAMP,.sample_mode=PL_TEX_SAMPLE_NEAREST},
    };
    const float strengths[] = {0.10f,0.15f,0.20f,0.30f};
    float strength = s_fgsim_mode ? strengths[s_strength] : s_strength/100.0f;
    float frame = (float)((uint64_t)llround(s_frame_seconds*s_fps_num/s_fps_den)%512);
    float offset[] = {s_plate_origin_x,s_plate_origin_y};
    const struct pl_shader_var uniforms[] = {
        {.var=pl_var_float("fgsStrength"),.data=&strength,.dynamic=true},
        {.var=pl_var_float("fgsFrame"),.data=&frame,.dynamic=true},
        {.var=pl_var_vec2("fgsOffset"),.data=offset,.dynamic=true},
    };
    // RGB here is still in the source transfer function, before display mapping.
    const char fgsim_sdr[] =
        "color = fgsim_apply(color, ivec2(floor(gl_FragCoord.xy + fgsOffset)), int(fgsFrame), fgsStrength);\n";
    const char fgsim_hdr[] =
        "vec4 effected = fgsim_apply(color, ivec2(floor(gl_FragCoord.xy + fgsOffset)), int(fgsFrame), fgsStrength);\n"
        "vec3 weights = vec3(0.2627, 0.6780, 0.0593);\n"
        "float span = FGS_Y_SPAN, black = FGS_Y_BLACK;\n"
        "float y = floor(clamp(dot(color.rgb, weights)*span+black,0.0,1023.0)+0.5);\n"
        "float fy = floor(clamp(dot(effected.rgb, weights)*span+black,0.0,1023.0)+0.5);\n"
        "float delta = clamp(512.0+y-fy,0.0,1023.0)-512.0;\n"
        "color.rgb += vec3((clamp(y+delta,0.0,1023.0)-y)/span);\n";
    const char digital[] =
        "ivec2 px = ivec2(floor(gl_FragCoord.xy + fgsOffset));\n"
        "px = clamp(px, ivec2(0), textureSize(fgsNoise,0)-ivec2(1));\n"
        "float n = floor(texelFetch(fgsNoise,px,0).r*255.0+0.5);\n"
        "vec3 weights = FGS_WEIGHTS;\n"
        "float luma = dot(color.rgb,weights);\n"
        "float maskY = clamp(floor(luma*219.0+16.5),0.0,255.0);\n"
        "float mask = floor(fgsStrength*(182.0-abs(75.0-maskY))+0.0001);\n"
        "float alpha8 = 255.0-floor(n*mask/255.0);\n"
        "#if FGS_HDR\n"
        "float alpha10 = floor(clamp((alpha8-16.0)*1023.0/219.0,0.0,1023.0)+0.5);\n"
        "float y = floor(clamp(luma*FGS_Y_SPAN+FGS_Y_BLACK,0.0,1023.0)+0.5);\n"
        "float outY = floor(FGS_Y_BLACK+(y-FGS_Y_BLACK)*alpha10/1023.0);\n"
        "color.rgb += vec3((outY-y)/FGS_Y_SPAN);\n"
        "#else\n"
        "color.rgb *= alpha8/255.0;\n"
        "#endif\n";
    char prelude[256];
    snprintf(prelude,sizeof(prelude),
        "#define FGS_Y_SPAN %s\n#define FGS_Y_BLACK %s\n#define FGS_HDR %d\n#define FGS_WEIGHTS %s\n",
        s_source_full?"1023.0":"876.0", s_source_full?"0.0":"64.0", !!s_hdr_transfer,
        s_hdr_transfer?"vec3(0.2627,0.6780,0.0593)":"vec3(0.2126,0.7152,0.0722)");
    bool okay = pl_shader_custom(params->sh,&(struct pl_custom_shader){
        .description=s_fgsim_mode?"FGSIM preview":"Digital Grain preview",
        .input=PL_SHADER_SIG_COLOR,.output=PL_SHADER_SIG_COLOR,.prelude=prelude,
        .header=s_fgsim_shader,
        .body=s_fgsim_mode ? (s_hdr_transfer?fgsim_hdr:fgsim_sdr) : digital,
        .descriptors=&texture,.num_descriptors=1,
        .variables=uniforms,.num_variables=sizeof(uniforms)/sizeof(uniforms[0]),
    });
    if (!okay) return (struct pl_hook_res){.failed=true};
    return (struct pl_hook_res){
        .output=PL_HOOK_SIG_COLOR,.sh=params->sh,.repr=params->repr,
        .color=params->color,.components=params->components,.rect=params->rect,
    };
}

static bool upload_pixel_noise(const unsigned char *raw, bool rgba)
{
    int components = rgba ? 4 : 1;
    int width = rgba ? 512 : s_width;
    int height = rgba ? 512 : s_height;
    struct pl_plane_data plane = {
        .type=PL_FMT_UNORM,.width=width,.height=height,
        .pixel_stride=components,.row_stride=(size_t)width*components,.pixels=raw,
    };
    int sizes[] = {8,rgba?8:0,rgba?8:0,rgba?8:0};
    int shifts[] = {0,8,16,24};
    pl_plane_data_from_comps(&plane,sizes,shifts);
    struct pl_plane uploaded;
    return pl_upload_plane(s_device->gpu,&uploaded,&s_pixel_noise,&plane);
}

/* A separate D3D11 child window keeps GDI slider painting completely outside
 * the GPU swapchain. Border-only view reserves a left GDI thumbnail sidebar.
 * No GDI-over-D3D overlays or repeated video uploads. */
static void track_bounds(RECT *client, int *left, int *right, int *top)
{
    *left = 144;
    *right = client->right - 150;
    if (*right < *left+24) *right = *left+24;
    *top = client->bottom-panel_height();
}

/* Center the frame controls in the space left of the action buttons. */
static void bottom_frame_controls(const RECT *client, RECT *edit,
                                  RECT *random_btn, RECT *seek_btn, int *hint_left)
{
    int height=s_time_edit_height>32?s_time_edit_height:32;
    // The border settings timeline toolbar lives immediately under its frame slider.
    bool border_toolbar=s_border_settings && s_video_path;
    int bottom=border_toolbar?client->bottom-panel_height()+66+height:client->bottom-12;
    int group_width=s_time_edit_width+12+106+12+106;
    int area_right=s_result_path && !border_toolbar?client->right-256:client->right-14;
    int x=14+(area_right-14-group_width)/2;
    int edit_top=bottom-height+(height-s_time_edit_height)/2;
    *edit=(RECT){x,edit_top,x+s_time_edit_width,edit_top+s_time_edit_height};
    *random_btn=(RECT){edit->right+12,bottom-height,edit->right+118,bottom};
    *seek_btn=(RECT){random_btn->right+12,bottom-height,random_btn->right+118,bottom};
    *hint_left=x;
}

static void action_buttons(const RECT *client, RECT *apply, RECT *cancel)
{
    int bottom = client->bottom-12;
    int height = s_time_edit_height > 32 ? s_time_edit_height : 32;
    *apply = (RECT){client->right-244,bottom-height,client->right-134,bottom};
    *cancel = (RECT){client->right-123,bottom-height,client->right-13,bottom};
}

/* Match the FGS dark ThemeTrackBar: green rail and a light rectangular thumb. */
static void draw_slider(HDC dc, RECT *client, int y, const wchar_t *name,
                        double fraction, bool enabled, const wchar_t *value, bool round_thumb)
{
    int left, right, top;
    track_bounds(client, &left, &right, &top);
    (void)top;
    if (fraction < 0) fraction = 0;
    if (fraction > 1) fraction = 1;
    SetBkMode(dc, TRANSPARENT);
    SetTextColor(dc, enabled ? RGB(235,237,240) : RGB(162,167,174));
    RECT label = {14, y-15, left-14, y+15};
    DrawTextW(dc, name, -1, &label, DT_RIGHT|DT_VCENTER|DT_SINGLELINE|DT_END_ELLIPSIS);
    RECT rail = {left,y-2,right,y+2};
    HBRUSH border=CreateSolidBrush(RGB(64,66,71));
    FillRect(dc,&rail,border);DeleteObject(border);
    int handle_x=left+(int)((right-left)*fraction+0.5);
    if (enabled) {
        RECT filled={left,y-2,handle_x,y+2};
        HBRUSH accent=CreateSolidBrush(RGB(0,151,111));
        FillRect(dc,&filled,accent);DeleteObject(accent);
    }
    if (round_thumb) {
        HBRUSH handle=CreateSolidBrush(enabled?RGB(0,151,111):RGB(162,167,174));
        HGDIOBJ old_brush=SelectObject(dc,handle),old_pen=SelectObject(dc,GetStockObject(NULL_PEN));
        Ellipse(dc,handle_x-7,y-7,handle_x+7,y+7);
        SelectObject(dc,old_pen);SelectObject(dc,old_brush);DeleteObject(handle);
    } else {
        RECT thumb={handle_x-6,y-10,handle_x+6,y+10};
        HBRUSH handle=CreateSolidBrush(enabled?RGB(235,237,240):RGB(162,167,174));
        FillRect(dc,&thumb,handle);DeleteObject(handle);
    }
    RECT amount={right+16,y-15,client->right-12,y+15};
    DrawTextW(dc,value,-1,&amount,DT_LEFT|DT_VCENTER|DT_SINGLELINE|DT_END_ELLIPSIS);
}

static void redraw_controls(void);
/* Separate composition controls; they do not change the existing 1:1 inspection mode. */
static void focus_buttons(const RECT *client,RECT *compose,RECT *reset)
{
    int y=client->bottom-panel_height()+244+border_timeline_shift();
    *compose=(RECT){144,y,client->right-160,y+28};
    *reset=(RECT){client->right-147,y,client->right-13,y+28};
}
/* Center the X/Y labels and fields as a single row; mirror the frame-time EDIT height. */
static void focus_edit_rects(const RECT *client,RECT *label_x,RECT *edit_x,
                             RECT *label_y,RECT *edit_y)
{
    const int label_width=68,field_width=110,label_gap=6,pair_gap=24;
    const int group_width=2*(label_width+label_gap+field_width)+pair_gap;
    const int left=(client->right-group_width)/2;
    const int top=client->bottom-panel_height()+318+border_timeline_shift();
    const int row_height=s_time_edit_height>25?s_time_edit_height:25;
    const int edit_top=top+(row_height-s_time_edit_height)/2;
    *label_x=(RECT){left,top,left+label_width,top+row_height};
    *edit_x=(RECT){label_x->right+label_gap,edit_top,
                    label_x->right+label_gap+field_width,edit_top+s_time_edit_height};
    *label_y=(RECT){edit_x->right+pair_gap,top,
                    edit_x->right+pair_gap+label_width,top+row_height};
    *edit_y=(RECT){label_y->right+label_gap,edit_top,
                    label_y->right+label_gap+field_width,edit_top+s_time_edit_height};
}
static void focus_edit_layout(const RECT *client)
{
    RECT label_x,edit_x,label_y,edit_y;
    focus_edit_rects(client,&label_x,&edit_x,&label_y,&edit_y);
    if(s_focus_x_edit)MoveWindow(s_focus_x_edit,edit_x.left,edit_x.top,
        edit_x.right-edit_x.left,edit_x.bottom-edit_x.top,TRUE);
    if(s_focus_y_edit)MoveWindow(s_focus_y_edit,edit_y.left,edit_y.top,
        edit_y.right-edit_y.left,edit_y.bottom-edit_y.top,TRUE);
}
static void sync_focus_edits(void)
{
    wchar_t value[48];
    if(s_focus_x_edit && GetFocus()!=s_focus_x_edit) {
        swprintf(value,48,L"%.4f",s_focus_x);
        SetWindowTextW(s_focus_x_edit,value);
    }
    if(s_focus_y_edit && GetFocus()!=s_focus_y_edit) {
        swprintf(value,48,L"%.4f",s_focus_y);
        SetWindowTextW(s_focus_y_edit,value);
    }
}
static void commit_focus_edit(HWND edit)
{
    if(edit!=s_focus_x_edit && edit!=s_focus_y_edit)return;
    wchar_t input[64];
    GetWindowTextW(edit,input,64);
    wchar_t *end=NULL;
    double value=wcstod(input,&end);
    if(end!=input && !*end && isfinite(value)) {
        value=fgs_focus_clamp(value,0,1);
        if(edit==s_focus_x_edit)s_focus_x=value;
        else s_focus_y=value;
    }
    sync_focus_edits();
    redraw_controls();
}
static LRESULT CALLBACK focus_edit_proc(HWND edit,UINT msg,WPARAM wparam,LPARAM lparam)
{
    if(msg==WM_KEYDOWN && wparam==VK_RETURN) {
        SetFocus(s_window); /* EN_KILLFOCUS commits; never applies the whole window. */
        return 0;
    }
    if(msg==WM_KEYDOWN && wparam==VK_ESCAPE) {
        wchar_t value[48];
        swprintf(value,48,L"%.4f",edit==s_focus_x_edit?s_focus_x:s_focus_y);
        SetWindowTextW(edit,value);
        SetFocus(s_window);
        return 0;
    }
    return CallWindowProcW(s_original_focus_edit_proc,edit,msg,wparam,lparam);
}

static void paint_sliders(HWND window)
{
    PAINTSTRUCT ps;
    HDC target = BeginPaint(window, &ps);
    RECT client;
    GetClientRect(window, &client);
    RECT panel = {0, client.bottom-panel_height(), client.right, client.bottom};
    /* Buffer only the panel, leaving the D3D child and video untouched. */
    HDC memory=CreateCompatibleDC(target);
    HBITMAP bitmap=memory?CreateCompatibleBitmap(target,client.right,panel_height()):NULL;
    HGDIOBJ old_bitmap=bitmap?SelectObject(memory,bitmap):NULL;
    HDC dc=bitmap?memory:target;
    if (bitmap) SetWindowOrgEx(dc,0,panel.top,NULL);
    FillRect(dc, &panel, s_panel_brush);
    HGDIOBJ old_font = s_panel_font ? SelectObject(dc, s_panel_font) : NULL;
    int y = panel.top+28;
    if (s_video_path) {
        wchar_t duration[80];
        swprintf(duration,80,L"%.1f / %.1f s",s_pending_seconds,s_duration_seconds);
        draw_slider(dc,&client,y,L"选择视频帧",
                    s_duration_seconds>0?s_pending_seconds/s_duration_seconds:0.0,true,duration,true);
        y+=40;
    }
    if (s_border_settings && s_border_list) {
        RECT import,remove,edit,rebuild;
        border_choice_buttons(&client,&import,&remove,&edit,&rebuild);
        RECT label={14,import.top,133,import.bottom};
        SetBkMode(dc,TRANSPARENT);
        SetTextColor(dc,RGB(235,237,240));
        DrawTextW(dc,L"边框模式",-1,&label,DT_RIGHT|DT_VCENTER|DT_SINGLELINE);
        RECT static_btn,weave_btn,off_btn;
        border_mode_buttons(&client,&static_btn,&weave_btn,&off_btn);
        HBRUSH normal=CreateSolidBrush(RGB(27,28,30));
        HBRUSH disabled=CreateSolidBrush(RGB(47,48,50));
        HBRUSH selected=CreateSolidBrush(RGB(0,111,85));
        HBRUSH edge=CreateSolidBrush(RGB(64,66,71));
        FillRect(dc,&static_btn,s_border_selection && !s_border_weave?selected:normal);
        FillRect(dc,&weave_btn,s_border_selection && s_border_weave?selected:normal);
        FillRect(dc,&off_btn,!s_border_selection?selected:normal);
        FrameRect(dc,&static_btn,edge);FrameRect(dc,&weave_btn,edge);FrameRect(dc,&off_btn,edge);
        SetTextColor(dc,RGB(235,237,240));
        DrawTextW(dc,L"静态",-1,&static_btn,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
        DrawTextW(dc,L"动态",-1,&weave_btn,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
        DrawTextW(dc,L"关闭",-1,&off_btn,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
        FillRect(dc,&rebuild,normal);
        FillRect(dc,&import,normal);
        bool can_delete=s_border_selection>=0 && s_border_selection<s_border_choices_count &&
            s_border_choices[s_border_selection].user;
        FillRect(dc,&remove,can_delete?normal:disabled);
        bool classic=!strncmp(s_border_choices[s_border_selection].mode,"CLASSIC_",8);
        FillRect(dc,&edit,classic?normal:disabled);
        FrameRect(dc,&rebuild,edge);FrameRect(dc,&import,edge);
        FrameRect(dc,&remove,edge);FrameRect(dc,&edit,edge);
        SetTextColor(dc,RGB(235,237,240));
        DrawTextW(dc,L"重建缩略图",-1,&rebuild,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
        DrawTextW(dc,L"导入...",-1,&import,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
        SetTextColor(dc,can_delete?RGB(235,237,240):RGB(135,139,143));
        DrawTextW(dc,L"删除",-1,&remove,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
        SetTextColor(dc,classic?RGB(235,237,240):RGB(135,139,143));
        DrawTextW(dc,L"编辑边印文字",-1,&edit,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
        DeleteObject(normal);DeleteObject(disabled);DeleteObject(selected);DeleteObject(edge);
        wchar_t motion_value[40];
        swprintf(motion_value,40,L"%.2f x",s_motion_amp);
        draw_slider(dc,&client,panel.top+130+border_timeline_shift(),L"晃动幅度",s_motion_amp/3.,true,motion_value,false);
        swprintf(motion_value,40,L"%.2f x",s_motion_speed);
        draw_slider(dc,&client,panel.top+170+border_timeline_shift(),L"晃动速度",(s_motion_speed-.25)/2.75,true,motion_value,false);
        swprintf(motion_value,40,L"%.2f x",s_motion_frequency);
        draw_slider(dc,&client,panel.top+210+border_timeline_shift(),L"随机频率",(s_motion_frequency-.25)/2.75,true,motion_value,false);
        RECT compose,reset;focus_buttons(&client,&compose,&reset);
        HBRUSH focus_brush=CreateSolidBrush(s_compose_mode?RGB(0,111,85):RGB(27,28,30));
        HBRUSH focus_gray=CreateSolidBrush(RGB(27,28,30));
        HBRUSH focus_edge=CreateSolidBrush(RGB(64,66,71));
        FillRect(dc,&compose,focus_brush);FillRect(dc,&reset,focus_gray);
        FrameRect(dc,&compose,focus_edge);FrameRect(dc,&reset,focus_edge);
        SetTextColor(dc,RGB(235,237,240));
        DrawTextW(dc,s_compose_mode?L"画面构图 · 拖动画面":L"画面构图 · 点击启用",-1,&compose,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
        DrawTextW(dc,L"恢复居中 1.00×",-1,&reset,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
        DeleteObject(focus_brush);DeleteObject(focus_gray);DeleteObject(focus_edge);
        wchar_t zoom_value[40];swprintf(zoom_value,40,L"%.3f x",s_focus_zoom);
        draw_slider(dc,&client,panel.top+295+border_timeline_shift(),L"画面缩放",(s_focus_zoom-1)/2,true,zoom_value,false);
        RECT label_x,edit_x,label_y,edit_y;
        focus_edit_rects(&client,&label_x,&edit_x,&label_y,&edit_y);
        DrawTextW(dc,L"X (0–1)",-1,&label_x,DT_LEFT|DT_VCENTER|DT_SINGLELINE);
        DrawTextW(dc,L"Y (0–1)",-1,&label_y,DT_LEFT|DT_VCENTER|DT_SINGLELINE);
    } else if (s_border_settings) {
        const wchar_t *mode=s_border_mode==2?L"动态 · Gate Weave":s_border_mode==1?L"静态 · Dark":L"关闭";
        draw_slider(dc,&client,y,L"添加边框",s_border_mode/2.0,true,mode,false);
    } else {
    wchar_t grain_value[32];
    if (s_fgsim_mode) swprintf(grain_value,32,L"%ls",s_display_grain?s_fgsim_names[s_strength]:L"关闭 · G");
    else if (s_digital_mode) {
        if (s_display_grain) swprintf(grain_value,32,L"%.2f",s_strength/100.0);
        else swprintf(grain_value,32,L"关闭 · G");
    } else swprintf(grain_value,32,L"%d%%",s_display_grain?s_strength:0);
    int grain_position = s_fgsim_mode ? s_strength*100/3 :
        s_digital_mode ? (s_strength-15)*100/85 : s_strength;
    draw_slider(dc,&client,y,s_fgsim_mode?L"FGSIM 档位":L"颗粒强度",
                (s_display_grain?grain_position:0)/100.0,s_has_grain,grain_value,false);
    wchar_t lut_value[32];
    swprintf(lut_value,32,L"%d%%",s_lut_source && s_lut_enabled?s_lut_strength:0);
    draw_slider(dc,&client,y+40,s_combined_grade?L"LUT 强度 *":L"LUT 强度",
                (s_lut_source && s_lut_enabled?s_lut_strength:0)/100.0,
                s_lut_source!=NULL,lut_value,false);
    }
    RECT edit, random_btn, seek_btn;
    int hint_left = 14;
    if (s_video_path) {
        bottom_frame_controls(&client,&edit,&random_btn,&seek_btn,&hint_left);
        HBRUSH gray=CreateSolidBrush(RGB(27,28,30));
        FillRect(dc,&random_btn,gray);FillRect(dc,&seek_btn,gray);
        SetTextColor(dc,RGB(235,237,240));
        DrawTextW(dc,L"随机一帧",-1,&random_btn,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
        DrawTextW(dc,L"跳转",-1,&seek_btn,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
        DeleteObject(gray);
    }
    // Place the 1:1 hint above the timeline controls, rather than at the panel foot.
    bool border_toolbar=s_border_settings && s_video_path;
    RECT info=border_toolbar
        ? (RECT){14,panel.top+44,client.right-14,panel.top+62}
        : (RECT){14,panel.top+(s_border_settings?348:126),
                 s_result_path?client.right-256:client.right-14,client.bottom-50};
    SetBkMode(dc,TRANSPARENT);
    if (s_combined_grade) {
        RECT notice=info;notice.bottom=notice.top+17;
        SetTextColor(dc,RGB(255,181,83));
        DrawTextW(dc,s_hint_en
            ? L"* Combined grade: LUT controls are preview-only. Apply saves grain settings."
            : L"* 合成调色：LUT 强度与开关仅供预览，应用时回传颗粒设置。",
            -1,&notice,DT_CENTER|DT_VCENTER|DT_SINGLELINE|DT_END_ELLIPSIS|DT_NOPREFIX);
        info.top=notice.bottom;
    }
    SetTextColor(dc,s_frame_loading?RGB(255,181,83):RGB(162,167,174));
    const wchar_t *hint_text = s_frame_loading ? L"正在抽取所选视频帧…" :
        s_border_settings && s_compose_mode ? L"构图模式：拖动透明画窗中的视频；滚轮以鼠标位置缩放" :
        s_hint_en ? (s_pixel_view ? L"Double-click: fit  |  Drag: pan 1:1" : L"Double-click: 1:1 preview") :
        (s_pixel_view ? L"双击：适应窗口  |  拖动：平移 1:1" : L"双击画面：切换 1:1 预览");
    DrawTextW(dc,hint_text,-1,&info,DT_CENTER|DT_VCENTER|DT_SINGLELINE|DT_END_ELLIPSIS|DT_NOPREFIX);
    if (s_result_path) {
        RECT apply,cancel;action_buttons(&client,&apply,&cancel);
        HBRUSH green=CreateSolidBrush(RGB(0,151,111));
        HBRUSH gray=CreateSolidBrush(RGB(27,28,30));
        HBRUSH border=CreateSolidBrush(RGB(64,66,71));
        FillRect(dc,&apply,green);FillRect(dc,&cancel,gray);FrameRect(dc,&cancel,border);
        SetTextColor(dc,RGB(235,237,240));
        DrawTextW(dc,L"应用到 FGS",-1,&apply,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
        DrawTextW(dc,L"取消",-1,&cancel,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
        DeleteObject(green);DeleteObject(gray);DeleteObject(border);
    }
    if (old_font) SelectObject(dc, old_font);
    if (bitmap) {
        BitBlt(target,0,panel.top,client.right,panel_height(),dc,0,panel.top,SRCCOPY);
        SelectObject(memory,old_bitmap);DeleteObject(bitmap);
    }
    if (memory) DeleteDC(memory);
    if(s_border_settings && s_border_list)paint_border_sidebar(target,&client);
    EndPaint(window, &ps);
}

static void redraw_controls(void)
{
    if (s_window) {
        RECT client; GetClientRect(s_window, &client);
        RECT panel = {0, client.bottom-panel_height(), client.right, client.bottom};
        InvalidateRect(s_window, &panel, FALSE);
    }
    if (s_view) InvalidateRect(s_view, NULL, FALSE);
}

static void format_frame_time(double seconds, wchar_t *text, size_t capacity)
{
    long long milliseconds=(long long)llround(seconds*1000.0);
    if (milliseconds<0) milliseconds=0;
    long long hours=milliseconds/3600000;
    int minutes=(int)(milliseconds/60000%60);
    int secs=(int)(milliseconds/1000%60);
    int fraction=(int)(milliseconds%1000);
    swprintf(text,capacity,L"%02lld:%02d:%02d.%03d",hours,minutes,secs,fraction);
}

static void update_frame_time_text(double seconds)
{
    if (!s_time_edit) return;
    wchar_t value[64];
    format_frame_time(seconds,value,64);
    SetWindowTextW(s_time_edit,value);
}

static bool parse_frame_time(const wchar_t *text, double *seconds)
{
    int hours,minutes;double last;wchar_t extra;
    if (swscanf(text,L"%d:%d:%lf%lc",&hours,&minutes,&last,&extra)==3) {
        if (hours<0 || minutes<0 || minutes>=60 || !isfinite(last) || last<0 || last>=60)
            return false;
        *seconds=hours*3600.0+minutes*60.0+last;
        return true;
    }
    wchar_t *end;
    double value=wcstod(text,&end);
    if (end==text || *end || !isfinite(value) || value<0) return false;
    *seconds=value;
    return true;
}

/* FFmpeg starts only on explicit frame changes. Launch the source and plate
 * decoders before waiting for either, to avoid serial seek/decode latency.
 * Paths go directly to CreateProcessW, without CMD parsing. */
static bool start_frame_decoder(double seconds, bool plate, PROCESS_INFORMATION *process)
{
    const wchar_t *source = plate ? s_plate_path : s_video_path;
    const wchar_t *output = plate ? s_plate_frame_path : s_frame_path;
    if (!source || !output) return false;
    size_t capacity = wcslen(s_ffmpeg_path)+wcslen(source)+wcslen(output)+550;
    wchar_t *command = calloc(capacity,sizeof(wchar_t));
    if (!command) return false;
    if (plate) {
        /* ProRes short loops seek within one cycle. Keep -stream_loop for
         * frame-boundary safety and for all other plate formats. */
        double plate_seconds = s_plate_cycle_seconds>0 ?
            fmod(seconds,s_plate_cycle_seconds) : seconds;
        swprintf(command,capacity,
            L"\"%ls\" -hide_banner -nostdin -v error -y -stream_loop -1 "
            L"-ss %.6f -i \"%ls\" -map 0:v:0 -an -sn -dn -frames:v 1 "
            L"-vf \"fps=%u/%u,scale=%d:%d:flags=bilinear,format=yuv420p10le\" "
            L"-f rawvideo \"%ls\"",
            s_ffmpeg_path,plate_seconds,source,s_fps_num,s_fps_den,s_width,s_height,output);
    } else {
        swprintf(command,capacity,
            L"\"%ls\" -hide_banner -nostdin -v error -y -ss %.6f -i \"%ls\" "
            L"-map 0:v:0 -an -sn -dn -frames:v 1 "
            L"-vf \"%lsscale=%d:%d:flags=bicubic%ls,format=yuv420p%ls\" "
            L"-f rawvideo \"%ls\"",
            s_ffmpeg_path,seconds,source,s_deinterlace,s_width,s_height,
            !s_hdr_transfer ? L"" : s_source_full ? L":in_range=full:out_range=full" : L":in_range=limited:out_range=limited",
            s_frame_depth==10?L"10le":L"",output);
    }
    STARTUPINFOW startup={0};
    startup.cb=sizeof(startup);
    bool okay=CreateProcessW(s_ffmpeg_path,command,NULL,NULL,FALSE,CREATE_NO_WINDOW,
                            NULL,NULL,&startup,process)!=0;
    free(command);
    return okay;
}

/* Windows records each child's creation/exit times, so measurements remain
 * accurate even if the second child finishes before we wait on its handle. */
static bool finish_frame_decoder(PROCESS_INFORMATION *process, double *elapsed_ms)
{
    DWORD waited=WaitForSingleObject(process->hProcess,120000);
    DWORD exit_code=1;
    if (waited==WAIT_TIMEOUT) {
        TerminateProcess(process->hProcess,1);
        WaitForSingleObject(process->hProcess,INFINITE);
    } else if (waited==WAIT_OBJECT_0) GetExitCodeProcess(process->hProcess,&exit_code);
    FILETIME created,exited,kernel,user;
    *elapsed_ms=-1.0;
    if (GetProcessTimes(process->hProcess,&created,&exited,&kernel,&user)) {
        ULARGE_INTEGER beginning,ending;
        beginning.LowPart=created.dwLowDateTime;
        beginning.HighPart=created.dwHighDateTime;
        ending.LowPart=exited.dwLowDateTime;
        ending.HighPart=exited.dwHighDateTime;
        if (ending.QuadPart>=beginning.QuadPart)
            *elapsed_ms=(double)(ending.QuadPart-beginning.QuadPart)/10000.0;
    }
    CloseHandle(process->hThread);
    CloseHandle(process->hProcess);
    return waited==WAIT_OBJECT_0 && exit_code==0;
}

/* The formal noise branch has a constant input. Generate it independently
 * of video, and keep only its luma texture for all strength adjustments. */
static bool load_digital_noise(double seconds)
{
    unsigned seed = 1u+(unsigned)((uint64_t)llround(seconds*s_fps_num/s_fps_den)%2147483646u);
    int width = (((s_width*4+2)/3)+1)&~1;
    int height = (((s_height*4+2)/3)+1)&~1;
    size_t capacity = wcslen(s_noise_ffmpeg)+wcslen(s_digital_path)+600;
    wchar_t *command = calloc(capacity,sizeof(*command));
    if (!command) return false;
    swprintf(command,capacity,
        L"\"%ls\" -hide_banner -nostdin -v error -y -f lavfi -i "
        L"\"nullsrc=s=%dx%d,format=yuv420p,lutyuv=y=128:u=128:v=128,"
        L"noise=all_seed=%u:c0s=100:c0f=t+u,deflate=threshold0=15,"
        L"dilation=threshold0=10,eq=contrast=3,scale=%d:%d,format=yuv420p\" "
        L"-frames:v 1 -f rawvideo \"%ls\"",
        s_noise_ffmpeg,width,height,seed,s_width,s_height,s_digital_path);
    STARTUPINFOW startup={0};
    startup.cb=sizeof(startup);
    PROCESS_INFORMATION process={0};
    bool okay=CreateProcessW(s_noise_ffmpeg,command,NULL,NULL,FALSE,CREATE_NO_WINDOW,
                            NULL,NULL,&startup,&process)!=0;
    free(command);
    double elapsed_ms;
    if (!okay || !finish_frame_decoder(&process,&elapsed_ms)) return false;
    size_t bytes=(size_t)s_width*s_height*3/2;
    unsigned char *raw=NULL;
    okay=read_file(s_digital_path,bytes,bytes,&raw,NULL) && upload_pixel_noise(raw,false);
    free(raw);
    DeleteFileW(s_digital_path);
    return okay;
}

static bool decode_selected_frame(double seconds)
{
    if (!s_video_path || !s_ffmpeg_path || !s_frame_path || s_frame_loading) return false;
    if (seconds<0 || seconds>s_duration_seconds) return false;
    if (s_duration_seconds>0 && seconds>=s_duration_seconds)
        seconds=fmax(0.0,s_duration_seconds-0.05);
    if (fabs(seconds-s_frame_seconds)<0.0005) return true;

    FgsGrainModel next_model;
    uint64_t next_event_start = 0;
    char error[256];
    if (s_table_data && !fgs_parse_table_at_event(s_table_data,
            (uint64_t)llround(seconds*10000000.0),&next_model,
            &next_event_start,error,sizeof(error))) {
        s_pending_seconds=s_frame_seconds;
        update_frame_time_text(s_frame_seconds);
        InvalidateRect(s_window,NULL,FALSE);
        MessageBoxW(s_window,L"选定时间没有有效的 AV1 颗粒模型，请选择其他时间。",
                    L"FGS GPU Preview",MB_OK|MB_ICONWARNING);
        return false;
    }

    s_frame_loading=true;
    double started=timer_ms();
    InvalidateRect(s_window,NULL,FALSE);UpdateWindow(s_window);
    PROCESS_INFORMATION source_process={0},plate_process={0};
    bool source_started=start_frame_decoder(seconds,false,&source_process);
    bool plate_started=!s_plate_mode || start_frame_decoder(seconds,true,&plate_process);
    double source_ms=-1.0,plate_ms=-1.0;
    bool source_ok=source_started && finish_frame_decoder(&source_process,&source_ms);
    bool plate_ok=plate_started;
    if (s_plate_mode && plate_started)
        plate_ok=finish_frame_decoder(&plate_process,&plate_ms);
    bool okay=source_ok && plate_ok;
    double decode_ms=timer_ms()-started;
    printf("FRAME_DECODE,%.3f,%.3f,%.3f\n",source_ms,plate_ms,decode_ms);
    fflush(stdout);
    if (okay) {
        size_t expected=(size_t)s_width*s_height*3/2*(s_frame_depth==10?2:1);
        unsigned char *raw=NULL;
        if (read_file(s_frame_path,expected,expected,&raw,NULL)) {
            unsigned char *plate_raw=NULL;
            if (s_plate_mode && !read_file(s_plate_frame_path,expected,expected,&plate_raw,NULL))
                okay=false;
            if (okay && s_plate_mode) okay=upload_plate(plate_raw);
            if (okay && s_digital_mode) okay=load_digital_noise(seconds);
            if (okay) okay=upload_frame(raw,s_frame_depth);
            free(plate_raw);
            free(raw);
        } else okay=false;
    }
    if (okay) {
        if (s_table_data) {s_original=next_model;s_grain_event_start=next_event_start;}
        s_frame_seconds=s_pending_seconds=seconds;
        update_frame_time_text(seconds);
        printf("FRAME,0,%.3f,%.1f,%.1f\n",timer_ms()-started,process_memory_mb(),process_cpu_ms());
        fflush(stdout);
    } else {
        s_pending_seconds=s_frame_seconds;
        update_frame_time_text(s_frame_seconds);
        MessageBoxW(s_window,L"视频抽帧失败，仍显示上一帧。请检查 FFmpeg、时间点及源文件。",
                    L"FGS GPU Preview",MB_OK|MB_ICONWARNING);
    }
    s_frame_loading=false;
    redraw_controls();
    return okay;
}

static void change_frame_position(int x)
{
    if (!s_video_path || s_duration_seconds<=0) return;
    RECT client;GetClientRect(s_window,&client);
    int left,right,top;track_bounds(&client,&left,&right,&top);
    double fraction=(double)(x-left)/(right-left);
    if (fraction<0) fraction=0;
    if (fraction>1) fraction=1;
    s_pending_seconds=s_duration_seconds*fraction;
    update_frame_time_text(s_pending_seconds);
    InvalidateRect(s_window,NULL,FALSE);
}

static void seek_frame_from_edit(void)
{
    if (!s_time_edit) return;
    wchar_t value[80];double seconds;
    GetWindowTextW(s_time_edit,value,80);
    if (!parse_frame_time(value,&seconds) || seconds>s_duration_seconds) {
        MessageBoxW(s_window,L"时间格式应为 时:分:秒.毫秒，且不能超出视频时长。",
                    L"FGS GPU Preview",MB_OK|MB_ICONWARNING);
        return;
    }
    s_pending_seconds=seconds;
    decode_selected_frame(seconds);
}

static LRESULT CALLBACK frame_edit_proc(HWND window,UINT msg,WPARAM wparam,LPARAM lparam)
{
    if (msg==WM_KEYDOWN && wparam==VK_RETURN) {
        PostMessageW(s_window,MSG_SEEK_FRAME,0,0);
        return 0;
    }
    if (msg==WM_KEYDOWN && wparam==VK_ESCAPE) {
        PostMessageW(s_window,WM_KEYDOWN,VK_ESCAPE,0);
        return 0;
    }
    return CallWindowProcW(s_original_edit_proc,window,msg,wparam,lparam);
}

static void change_slider(int kind, int x)
{
    if (kind == 2 && !s_lut_source) return;
    if (kind == 1 && !s_has_grain) return;
    if (kind == 3) {change_frame_position(x);return;}
    if(kind==8) {
        RECT client;GetClientRect(s_window,&client);
        int left,right,top;track_bounds(&client,&left,&right,&top);
        double fraction=fgs_focus_clamp((double)(x-left)/(right-left),0,1);
        s_focus_zoom=floor((1+2*fraction)*1000.+0.5)/1000.;
        redraw_controls();return;
    }
    if(kind>=5 && kind<=7) {
        RECT client;GetClientRect(s_window,&client);
        int left,right,top;track_bounds(&client,&left,&right,&top);
        double fraction=(double)(x-left)/(right-left);
        if(fraction<0)fraction=0;
        if(fraction>1)fraction=1;
        double value=kind==5?fraction*3:0.25+fraction*2.75;
        value=floor(value*100.+.5)/100.;
        if(kind==5)s_motion_amp=value;
        else if(kind==6)s_motion_speed=value;
        else s_motion_frequency=value;
        redraw_controls();return;
    }
    if (kind == 4) {
        RECT client;GetClientRect(s_window,&client);
        int left,right,top;track_bounds(&client,&left,&right,&top);
        int mode=((x-left)*2+(right-left)/2)/(right-left);
        if(mode<0)mode=0;
        if(mode>2)mode=2;
        if(s_border_mode!=mode) {
            s_border_mode=mode;s_border_weave=mode==2;redraw_controls();
        }
        return;
    }
    RECT client; GetClientRect(s_window, &client);
    int left, right, top;
    track_bounds(&client, &left, &right, &top);
    (void)top;
    int percent = ((x-left)*100+(right-left)/2)/(right-left);
    if (percent < 0) percent = 0;
    if (percent > 100) percent = 100;
    if (kind == 1) {
        if (s_fgsim_mode) percent=(percent*3+50)/100;
        if (s_digital_mode) percent=15+5*((percent*17+50)/100);
        if (s_strength == percent && s_display_grain) return;
        s_strength = percent; s_display_grain = true;
    } else {
        if (s_lut_strength == percent && s_lut_enabled) return;
        s_lut_strength = percent; s_lut_enabled = true;
    }
    redraw_controls();
}

static void update_hdr_display(void)
{
    if (!s_hdr_transfer) return;
    bool hdr = false;
    IDXGISwapChain *chain = pl_d3d11_swapchain_unwrap(s_swapchain);
    if (chain) {
        IDXGIOutput *output = NULL;
        if (SUCCEEDED(IDXGISwapChain_GetContainingOutput(chain, &output))) {
            IDXGIOutput6 *output6 = NULL;
            if (SUCCEEDED(IDXGIOutput_QueryInterface(output, &IID_IDXGIOutput6, (void **)&output6))) {
                DXGI_OUTPUT_DESC1 desc;
                if (SUCCEEDED(IDXGIOutput6_GetDesc1(output6, &desc)))
                    hdr = desc.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
                IDXGIOutput6_Release(output6);
            }
            IDXGIOutput_Release(output);
        }
        IDXGISwapChain_Release(chain);
    }
    if (hdr != s_hdr_display) {
        pl_swapchain_colorspace_hint(s_swapchain, hdr ? &pl_color_space_hdr10 : &pl_color_space_srgb);
        s_hdr_display = hdr;
    }
}

static bool upload_border_texture(void)
{
    unsigned char *rgba = NULL;
    const int component_bits = s_hdr_transfer ? 16 : 8;
    const int stride = component_bits / 2;
    const size_t bytes = 1920u * 1080u * stride;
    if (!read_file(s_border_path, bytes, bytes, &rgba, NULL)) return false;
    struct pl_plane plane;
    struct pl_plane_data data = {
        .type=PL_FMT_UNORM, .width=1920, .height=1080,
        .pixel_stride=stride, .row_stride=1920*stride, .pixels=rgba,
    };
    // libplacebo may normalize these component descriptors in place.
    int sizes[4] = {component_bits,component_bits,component_bits,component_bits};
    int shifts[4] = {0,component_bits,2*component_bits,3*component_bits};
    pl_plane_data_from_comps(&data, sizes, shifts);
    bool okay = pl_upload_plane(s_device->gpu, &plane, &s_border_texture, &data);
    free(rgba);
    return okay;
}

static bool border_has_classic_geometry(void)
{
    if (s_border_settings && s_border_list &&
        s_border_selection>=0 && s_border_selection<s_border_choices_count)
        return !strncmp(s_border_choices[s_border_selection].mode,"CLASSIC_",8) ||
               s_border_choices[s_border_selection].user;
    return s_initial_border_mode &&
        (!wcsncmp(s_initial_border_mode,L"CLASSIC_",8) ||
         !wcsncmp(s_initial_border_mode,L"USER_",5));
}
static void border_aperture(int *width,int *height)
{
    int w=s_preview_aperture_width?s_preview_aperture_width:1680;
    int h=s_preview_aperture_height?s_preview_aperture_height:
          (border_has_classic_geometry()?FGS_CLASSIC_APERTURE_HEIGHT_1080:990);
    if(s_border_settings && s_border_list &&
       s_border_selection>=0 && s_border_selection<s_border_choices_count) {
        w=s_border_choices[s_border_selection].aperture_width;
        h=s_border_choices[s_border_selection].aperture_height;
    }
    *width=((s_width*w+960)/1920)&~1;
    *height=((s_height*h+540)/1080)&~1;
    if(*width>s_width)*width=s_width&~1;
    if(*height>s_height)*height=s_height&~1;
}

/* Translate a cursor position from the fitted D3D11 child into aperture UVs. */
static bool focus_view_point(HWND view,int mouse_x,int mouse_y,double *u,double *v)
{
    if(!s_border_settings || !s_compose_mode || !s_border_list ||
       !strcmp(s_border_choices[s_border_selection].mode,"OFF"))return false;
    RECT client;GetClientRect(view,&client);
    if(!s_width||!s_height||!client.right||!client.bottom)return false;
    double display=fmin((double)client.right/s_width,(double)client.bottom/s_height);
    double left=(client.right-s_width*display)/2.0,top=(client.bottom-s_height*display)/2.0;
    double px=(mouse_x-left)/display,py=(mouse_y-top)/display;
    int aw,ah;border_aperture(&aw,&ah);
    struct fgs_film_motion motion=fgs_film_border_motion_adjusted(s_frame_seconds,s_border_weave,
        s_motion_amp,s_motion_speed,s_motion_frequency);
    px-=motion.shift_x;py-=motion.shift_y;
    *u=(px-(s_width-aw)/2.0)/aw;
    *v=(py-(s_height-ah)/2.0)/ah;
    return *u>=0 && *u<=1 && *v>=0 && *v<=1;
}

static bool compose_film_border(void)
{
    pl_dispatch_reset_frame(s_border_dispatch);
    pl_shader sh = pl_dispatch_begin(s_border_dispatch);
    struct fgs_film_border_geometry geometry =
        fgs_film_border_geometry_adjusted(s_width,s_height,s_frame_seconds,s_border_weave,
            s_motion_amp,s_motion_speed,s_motion_frequency);
    border_aperture(&geometry.aperture_width,&geometry.aperture_height);
    geometry.aperture_x=(s_width-geometry.aperture_width)/2;
    geometry.aperture_y=(s_height-geometry.aperture_height)/2;
    const float frame[] = {(float)s_width,(float)s_height};
    const float aperture[] = {(float)geometry.aperture_width,(float)geometry.aperture_height};
    const float inset[] = {(float)geometry.aperture_x,(float)geometry.aperture_y};
    const float shift[] = {(float)geometry.shift_x,(float)geometry.shift_y};
    const float focus[] = {(float)s_focus_x,(float)s_focus_y};
    const float zoom[] = {(float)s_focus_zoom,(float)s_focus_zoom};
    const struct pl_shader_var variables[] = {
        {.var=pl_var_vec2("fgsBorderSize"),.data=frame},
        {.var=pl_var_vec2("fgsBorderAperture"),.data=aperture},
        {.var=pl_var_vec2("fgsBorderInset"),.data=inset},
        {.var=pl_var_vec2("fgsBorderShift"),.data=shift,.dynamic=true},
        {.var=pl_var_vec2("fgsBorderFocus"),.data=focus,.dynamic=true},
        {.var=pl_var_vec2("fgsBorderZoom"),.data=zoom,.dynamic=true},
    };
    const struct pl_shader_desc textures[] = {
        {.desc={.name="fgsBorderPng",.type=PL_DESC_SAMPLED_TEX},
         .binding={.object=s_border_texture,.address_mode=PL_TEX_ADDRESS_CLAMP,.sample_mode=PL_TEX_SAMPLE_LINEAR}},
        {.desc={.name="fgsBorderContent",.type=PL_DESC_SAMPLED_TEX},
         .binding={.object=s_border_content,.address_mode=PL_TEX_ADDRESS_CLAMP,.sample_mode=PL_TEX_SAMPLE_LINEAR}},
    };
    const char shader[] =
        "vec2 p = gl_FragCoord.xy - fgsBorderShift;\n"
        "vec2 inset = p - fgsBorderInset;\n"
        "vec3 rgb = vec3(0.0);\n"
        "if (all(greaterThanEqual(p, vec2(0.0))) && all(lessThan(p, fgsBorderSize))) {\n"
        "    if (all(greaterThanEqual(inset, vec2(0.0))) && all(lessThan(inset, fgsBorderAperture))) {\n"
        "        float scale = max(fgsBorderAperture.x / fgsBorderSize.x, fgsBorderAperture.y / fgsBorderSize.y) * fgsBorderZoom.x;\n"
        "        vec2 extent = fgsBorderAperture / (fgsBorderSize * scale);\n"
        "        vec2 origin = clamp(fgsBorderFocus - 0.5 * extent, vec2(0.0), vec2(1.0) - extent);\n"
        "        vec2 uv = origin + (inset / fgsBorderAperture) * extent;\n"
        "        rgb = texture(fgsBorderContent, uv).rgb;\n"
        "    }\n"
        "    vec4 border = texture(fgsBorderPng, p / fgsBorderSize);\n"
        "    rgb = mix(rgb, border.rgb, border.a);\n"
        "}\n"
        "color = vec4(rgb, 1.0);\n";
    if (!pl_shader_custom(sh,&(struct pl_custom_shader){
        .description="FGS Film Border",
        .input=PL_SHADER_SIG_NONE,.output=PL_SHADER_SIG_COLOR,
        .output_w=s_width,.output_h=s_height,
        .body=shader,.descriptors=textures,.num_descriptors=2,
        .variables=variables,.num_variables=sizeof(variables)/sizeof(variables[0]),
    })) {
        pl_dispatch_abort(s_border_dispatch, &sh);
        return false;
    }
    return pl_dispatch_finish(s_border_dispatch, &(struct pl_dispatch_params){
        .shader=&sh,.target=s_border_composite,
    });
}

static bool init_film_border_targets(void)
{
    pl_gpu gpu = s_device->gpu;
    pl_fmt format = pl_find_fmt(gpu, PL_FMT_FLOAT, 4, 16, 0,
                               PL_FMT_CAP_SAMPLEABLE | PL_FMT_CAP_LINEAR | PL_FMT_CAP_RENDERABLE);
    if (!format) return false;
    struct pl_tex_params params = {
        .w=s_width,.h=s_height,.format=format,.sampleable=true,.renderable=true,
    };
    s_border_content = pl_tex_create(gpu,&params);
    s_border_composite = pl_tex_create(gpu,&params);
    s_border_dispatch = pl_dispatch_create(s_log,gpu);
    return s_border_content && s_border_composite && s_border_dispatch;
}

static bool render_film_border(struct pl_frame source, const struct pl_frame *target,
                               const struct pl_render_params *params)
{
    struct pl_frame intermediate = {
        .num_planes=1,.planes={{.texture=s_border_content,.components=4,.component_mapping={0,1,2,3}}},
        .repr=pl_color_repr_rgb,.color=source.color,.crop=s_image.crop,
    };
    pl_rect2df output_crop = source.crop;
    s_plate_origin_x = s_plate_origin_y = 0;
    source.crop = s_image.crop;
    if (!pl_render_image(s_renderer,&source,&intermediate,params) || !compose_film_border())
        return false;
    intermediate.planes[0].texture = s_border_composite;
    intermediate.crop = output_crop;
    return pl_render_image(s_renderer,&intermediate,target,&pl_render_default_params);
}

static double render_once(bool report)
{
    if (!s_renderer || !s_view) return -1;
    RECT rect;
    GetClientRect(s_view, &rect);
    int width = rect.right - rect.left, height = rect.bottom - rect.top;
    if (width < 1 || height < 1) return -1;
    if (width != s_swap_width || height != s_swap_height) {
        if (!pl_swapchain_resize(s_swapchain, &width, &height)) return -1;
        s_swap_width = width; s_swap_height = height;
    }
    update_hdr_display();
    double start = timer_ms();
    set_grain(s_strength);
    struct pl_swapchain_frame swap;
    if (!pl_swapchain_start_frame(s_swapchain, &swap)) return -1;
    struct pl_frame target;
    pl_frame_from_swapchain(&target, &swap);
    struct pl_frame source = s_image;
    if (s_pixel_view) {
        int shown_w = width < s_width ? width : s_width;
        int shown_h = height < s_height ? height : s_height;
        int max_pan_x = (s_width-shown_w)/2;
        int max_pan_y = (s_height-shown_h)/2;
        if (s_pan_x > max_pan_x) s_pan_x = max_pan_x;
        if (s_pan_x < -max_pan_x) s_pan_x = -max_pan_x;
        if (s_pan_y > max_pan_y) s_pan_y = max_pan_y;
        if (s_pan_y < -max_pan_y) s_pan_y = -max_pan_y;
        int left = (s_width-shown_w)/2-s_pan_x;
        int top = (s_height-shown_h)/2-s_pan_y;
        source.crop = (pl_rect2df){left,top,left+shown_w,top+shown_h};
        int dest_x = (width-shown_w)/2;
        int dest_y = (height-shown_h)/2;
        target.crop = (pl_rect2df){dest_x,dest_y,dest_x+shown_w,dest_y+shown_h};
    } else {
        pl_rect2df_aspect_copy_rot(&target.crop, &s_image.crop, 0.0f, s_image.rotation);
    }
    /* PL_HOOK_NATIVE sees the cropped intermediate's local src_rect, not
     * original frame coordinates. Preserve the full-frame origin ourselves. */
    s_plate_origin_x = source.crop.x0;
    s_plate_origin_y = source.crop.y0;
    struct pl_render_params params = pl_render_default_params;
    params.lut = selected_lut();
    params.lut_type = PL_LUT_NATIVE;
    const struct pl_hook *plate_hooks[] = {&s_plate_hook};
    const struct pl_hook *pixel_hooks[] = {&s_pixel_hook};
    if (s_plate_mode) {params.hooks=plate_hooks;params.num_hooks=1;}
    if (s_fgsim_mode || s_digital_mode) {
        if (!s_hdr_transfer) {
            source.lut=params.lut;
            source.lut_type=PL_LUT_NORMALIZED;
            params.lut=NULL;
        }
        if (s_display_grain) {params.hooks=pixel_hooks;params.num_hooks=1;}
    }
    bool border_visible=s_border_path && (!s_border_settings ||
        (s_border_list ? strcmp(s_border_choices[s_border_selection].mode,"OFF")!=0 : s_border_mode!=0));
    bool okay = border_visible ? render_film_border(source,&target,&params) :
        pl_render_image(s_renderer,&source,&target,&params);
    if (!okay || !pl_swapchain_submit_frame(s_swapchain)) {
        if (s_border_path) {
            fprintf(stderr,"Film border GPU render failed\n");
            PostQuitMessage(7);
        }
        return -1;
    }
    pl_swapchain_swap_buffers(s_swapchain);
    /* Completion latency, not merely CPU command-submission time. */
    pl_gpu_finish(s_device->gpu);
    double delta = timer_ms() - start;
    wchar_t title[256];
    wchar_t grain_label[40];
    if (s_fgsim_mode) swprintf(grain_label,40,L"FGSIM %ls",s_display_grain?s_fgsim_names[s_strength]:L"Off");
    else if (s_digital_mode) swprintf(grain_label,40,L"Digital Grain %.2f",s_display_grain?s_strength/100.0:0.0);
    else swprintf(grain_label,40,L"%ls %d%%",s_plate_mode?L"Plate":L"Grain",s_display_grain?s_strength:0);
    if(s_border_settings)
        swprintf(title,256,L"FGS 添加边框 | %ls | %ls | %.2f ms",
            s_border_list?s_border_choices[s_border_selection].title:
                s_border_mode==2?L"动态":s_border_mode==1?L"静态":L"关闭",
            s_pixel_view?L"1:1":L"Fit",delta);
    else swprintf(title, 256, L"FGS GPU Preview%ls%ls | %ls | %ls | LUT %d%% | %.2f ms | G / L / ESC",
             s_border_path ? (s_border_weave ? L" [Dark Weave]" : L" [Dark Static]") : L"",
             !s_hdr_transfer ? L"" : pl_color_transfer_is_hdr(swap.color_space.transfer) ? L" [HDR display]" : L" [HDR to SDR display]",
             s_pixel_view?L"1:1":L"Fit",
             grain_label,
             s_lut_enabled && s_lut_source ? s_lut_strength : 0, delta);
    SetWindowTextW(s_window, title);
    if (report) {
        printf("%s,%d,%.3f,%.1f,%.1f\n", s_first ? "FIRST" : "ADJUST",
               s_display_grain ? s_strength : 0, delta, process_memory_mb(), process_cpu_ms());
        fflush(stdout);
        s_first = false;
    }
    return delta;
}

static LRESULT CALLBACK view_proc(HWND window, UINT msg, WPARAM wparam, LPARAM lparam)
{
    switch (msg) {
    case WM_ERASEBKGND: return 1;
    case WM_PAINT: {
        PAINTSTRUCT paint;
        BeginPaint(window, &paint);
        EndPaint(window, &paint);
        if (!s_benchmark) render_once(true);
        return 0;
    }
    case WM_SIZE:
        InvalidateRect(window, NULL, FALSE);
        return 0;
    case WM_KEYDOWN:
        return SendMessageW(s_window, msg, wparam, lparam);
    case WM_LBUTTONDOWN:
        SetFocus(s_window);
        if(s_border_settings && s_compose_mode) {
            double u,v;
            int x=(short)LOWORD(lparam),y=(short)HIWORD(lparam);
            if(focus_view_point(window,x,y,&u,&v)) {
                s_focus_dragging=true;s_focus_last_x=x;s_focus_last_y=y;
                SetCapture(window);
            }
            return 0;
        }
        if (s_pixel_view) {
            s_panning=true;
            s_pan_last_x=(short)LOWORD(lparam);s_pan_last_y=(short)HIWORD(lparam);
            SetCapture(window);
        }
        return 0;
    case WM_LBUTTONDBLCLK:
        if(s_border_settings && s_compose_mode)return 0;
        if (s_panning && GetCapture()==window)ReleaseCapture();
        s_panning=false;s_pixel_view=!s_pixel_view;s_pan_x=s_pan_y=0;
        redraw_controls();
        return 0;
    case WM_MOUSEMOVE:
        if(s_focus_dragging && GetCapture()==window && (wparam&MK_LBUTTON)) {
            int x=(short)LOWORD(lparam),y=(short)HIWORD(lparam);
            RECT client;GetClientRect(window,&client);
            double scale=fmin((double)client.right/s_width,(double)client.bottom/s_height);
            if(scale>0) {
                int aw,ah;border_aperture(&aw,&ah);
                fgs_focus_pan(s_width,s_height,aw,ah,
                    (x-s_focus_last_x)/scale,(y-s_focus_last_y)/scale,
                    &s_focus_x,&s_focus_y,s_focus_zoom);
                s_focus_last_x=x;s_focus_last_y=y;
                sync_focus_edits();redraw_controls();
            }
            return 0;
        }
        if (s_panning && GetCapture()==window && (wparam&MK_LBUTTON)) {
            int x=(short)LOWORD(lparam),y=(short)HIWORD(lparam);
            if (x!=s_pan_last_x || y!=s_pan_last_y) {
                s_pan_x+=x-s_pan_last_x;s_pan_y+=y-s_pan_last_y;
                s_pan_last_x=x;s_pan_last_y=y;
                InvalidateRect(window,NULL,FALSE);
            }
        }
        return 0;
    case WM_LBUTTONUP:
        if(s_focus_dragging) {
            s_focus_dragging=false;
            if(GetCapture()==window)ReleaseCapture();
            return 0;
        }
        if (s_panning && GetCapture()==window)ReleaseCapture();
        s_panning=false;
        return 0;
    case WM_CAPTURECHANGED:
        s_panning=false;s_focus_dragging=false;
        return 0;
    case WM_MOUSEWHEEL:
        if(s_border_settings && s_compose_mode) {
            POINT pt={(short)LOWORD(lparam),(short)HIWORD(lparam)};
            ScreenToClient(window,&pt);
            double u,v;
            if(focus_view_point(window,pt.x,pt.y,&u,&v)) {
                double amount=(double)GET_WHEEL_DELTA_WPARAM(wparam)/WHEEL_DELTA;
                double new_zoom=fgs_focus_clamp(s_focus_zoom*pow(1.15,amount),1,3);
                int aw,ah;border_aperture(&aw,&ah);
                fgs_focus_zoom_at(s_width,s_height,aw,ah,u,v,new_zoom,
                    &s_focus_x,&s_focus_y,&s_focus_zoom);
                sync_focus_edits();redraw_controls();
            }
            return 0;
        }
        return DefWindowProcW(window,msg,wparam,lparam);
    default:
        return DefWindowProcW(window, msg, wparam, lparam);
    }
}

static LRESULT CALLBACK window_proc(HWND window, UINT msg, WPARAM wparam, LPARAM lparam)
{
    switch (msg) {
    case WM_LBUTTONDOWN: {
        if(s_border_settings && (GetFocus()==s_focus_x_edit || GetFocus()==s_focus_y_edit))
            SetFocus(window);
        RECT client; GetClientRect(window, &client);
        int top = client.bottom-panel_height();
        int y = (short)HIWORD(lparam);
        int x = (short)LOWORD(lparam);
        POINT point={x,y};
        if (s_result_path) {
            RECT apply,cancel;action_buttons(&client,&apply,&cancel);
            if (PtInRect(&apply,point)) {
                if (write_result()) DestroyWindow(window);
                else MessageBoxW(window, L"无法写入调整结果，请检查临时目录权限。",
                                 L"FGS GPU 预览", MB_ICONERROR|MB_OK);
                return 0;
            }
            if (PtInRect(&cancel,point)) {DestroyWindow(window);return 0;}
        }
        if(s_border_settings && s_border_list) {
            int arrow=border_sidebar_hit(&client,point);
            if(arrow) {
                s_border_arrow_hover=arrow;
                s_border_arrow_pressed=arrow;
                SetCapture(window);
                border_sidebar_redraw();
                UpdateWindow(window); // Draw the depressed state before mouse release.
                return 0;
            }
            RECT static_btn,weave_btn,off_btn;
            border_mode_buttons(&client,&static_btn,&weave_btn,&off_btn);
            if(PtInRect(&static_btn,point) || PtInRect(&weave_btn,point)) {
                if(!border_mode_apply(PtInRect(&weave_btn,point)))
                    MessageBoxW(window,L"切换边框模式失败。",L"添加边框",MB_OK|MB_ICONERROR);
                return 0;
            }
            if(PtInRect(&off_btn,point)) {
                if(!border_choice_apply(0,true))
                    MessageBoxW(window,L"关闭边框时恢复默认纹理失败，保留原样式。",L"添加边框",MB_OK|MB_ICONERROR);
                return 0;
            }
            RECT compose,reset;focus_buttons(&client,&compose,&reset);
            if(PtInRect(&compose,point)) {
                s_compose_mode=!s_compose_mode;
                if(s_compose_mode) {
                    s_compose_old_pixel_view=s_pixel_view;
                    s_compose_old_pan_x=s_pan_x;s_compose_old_pan_y=s_pan_y;
                    s_pixel_view=false;s_pan_x=s_pan_y=0;
                } else {
                    s_pixel_view=s_compose_old_pixel_view;
                    s_pan_x=s_compose_old_pan_x;s_pan_y=s_compose_old_pan_y;
                }
                redraw_controls();return 0;
            }
            if(PtInRect(&reset,point)) {
                s_focus_x=s_focus_y=0.5;s_focus_zoom=1;
                sync_focus_edits();redraw_controls();return 0;
            }
            RECT import,remove,edit,rebuild;
            border_choice_buttons(&client,&import,&remove,&edit,&rebuild);
            if(PtInRect(&rebuild,point)) {
                if(MessageBoxW(window,L"重新生成所有胶片边框缩略图？\n原始边框和编码设置不会修改。",
                   L"FGS 添加边框",MB_YESNO|MB_ICONQUESTION)==IDYES) {
                    s_border_action="REBUILD";
                    if(write_result())DestroyWindow(window);
                }
                return 0;
            }
            if(PtInRect(&import,point)) {
                s_border_action="IMPORT";
                if(write_result())DestroyWindow(window);
                return 0;
            }
            if(PtInRect(&edit,point)) {
                if(!strncmp(s_border_choices[s_border_selection].mode,"CLASSIC_",8)) {
                    s_border_action="EDIT";
                    if(write_result())DestroyWindow(window);
                }
                return 0;
            }
            if(PtInRect(&remove,point)) {
                if(s_border_choices[s_border_selection].user) {
                    s_border_action="DELETE";
                    if(write_result())DestroyWindow(window);
                }
                return 0;
            }
        }
        if (s_video_path) {
            RECT edit,random_btn,seek_btn;
            int hint_left;
            bottom_frame_controls(&client,&edit,&random_btn,&seek_btn,&hint_left);
            if (PtInRect(&random_btn,point)) {
                double limit=fmax(0.0,s_duration_seconds-0.05);
                s_pending_seconds=limit*((double)rand()/RAND_MAX);
                update_frame_time_text(s_pending_seconds);
                decode_selected_frame(s_pending_seconds);
                return 0;
            }
            if (PtInRect(&seek_btn,point)) {seek_frame_from_edit();return 0;}
        }
        int left, right, unused_top;
        track_bounds(&client, &left, &right, &unused_top);
        if (x < left-14 || x > right+14) return 0;
        int row = y >= top+8 && y < top+128 ? (y-top-8)/40 : -1;
        int border_shift=border_timeline_shift();
        int kind = s_border_settings ?
                   (s_border_list ? (y>=top+116+border_shift && y<top+146+border_shift ? 5 :
                                     y>=top+156+border_shift && y<top+186+border_shift ? 6 :
                                     y>=top+196+border_shift && y<top+226+border_shift ? 7 :
                                     y>=top+281+border_shift && y<top+309+border_shift ? 8 :
                                     s_video_path && row==0 ? 3 : 0) :
                    (s_video_path?(row==0?3:row==1?4:0):(row==0?4:0))) :
                   (s_video_path?(row==0?3:row==1?1:row==2?2:0):(row==0?1:row==1?2:0));
        if (kind == 2 && !s_lut_source) kind = 0;
        if (!kind) return 0;
        SetFocus(window);
        s_drag_kind = kind;
        SetCapture(window);
        change_slider(kind, x);
        return 0;
    }
    case WM_MOUSEMOVE:
        if(s_border_settings && s_border_list) {
            RECT client;GetClientRect(window,&client);
            POINT point={(short)LOWORD(lparam),(short)HIWORD(lparam)};
            int hover=border_sidebar_hit(&client,point);
            if(hover!=s_border_arrow_hover) {
                s_border_arrow_hover=hover;
                border_sidebar_redraw();
            }
            if(!s_border_arrow_tracking) {
                TRACKMOUSEEVENT track={sizeof(track),TME_LEAVE,window,0};
                if(TrackMouseEvent(&track))s_border_arrow_tracking=true;
            }
        }
        if (s_drag_kind && GetCapture() == window && (wparam & MK_LBUTTON))
            change_slider(s_drag_kind, (short)LOWORD(lparam));
        return 0;
    case WM_MOUSELEAVE:
        s_border_arrow_tracking=false;
        if(s_border_arrow_hover) {
            s_border_arrow_hover=0;
            border_sidebar_redraw();
        }
        return 0;
    case WM_LBUTTONUP:
        if(s_border_arrow_pressed) {
            int pressed=s_border_arrow_pressed;
            RECT client;GetClientRect(window,&client);
            POINT point={(short)LOWORD(lparam),(short)HIWORD(lparam)};
            bool clicked=border_sidebar_hit(&client,point)==pressed;
            s_border_arrow_pressed=0;
            if(GetCapture()==window)ReleaseCapture();
            if(clicked)border_list_scroll(pressed);
            border_sidebar_redraw();
            return 0;
        }
        if (s_drag_kind) {
            int kind=s_drag_kind;
            change_slider(s_drag_kind, (short)LOWORD(lparam));
            s_drag_kind = 0;
            if (GetCapture() == window) ReleaseCapture();
            if (kind==3) decode_selected_frame(s_pending_seconds);
        }
        return 0;
    case WM_CAPTURECHANGED:
        s_drag_kind = 0;
        if(s_border_arrow_pressed) {
            s_border_arrow_pressed=0;
            border_sidebar_redraw();
        }
        return 0;
    case WM_MOUSEWHEEL:
        if(s_border_settings && s_border_list) {
            POINT point={(short)LOWORD(lparam),(short)HIWORD(lparam)};
            ScreenToClient(window,&point);
            RECT client;GetClientRect(window,&client);
            if(point.x>=0 && point.x<BORDER_SIDEBAR_WIDTH &&
               point.y>=0 && point.y<client.bottom-panel_height()) {
                s_border_wheel_delta+=(short)GET_WHEEL_DELTA_WPARAM(wparam);
                while(s_border_wheel_delta>=WHEEL_DELTA) {
                    border_list_scroll_lines(-3);s_border_wheel_delta-=WHEEL_DELTA;
                }
                while(s_border_wheel_delta<=-WHEEL_DELTA) {
                    border_list_scroll_lines(3);s_border_wheel_delta+=WHEEL_DELTA;
                }
                return 0;
            }
        }
        return DefWindowProcW(window,msg,wparam,lparam);
    case WM_COMMAND:
        if(s_border_settings && HIWORD(wparam)==EN_KILLFOCUS &&
           ((HWND)lparam==s_focus_x_edit || (HWND)lparam==s_focus_y_edit)) {
            commit_focus_edit((HWND)lparam);return 0;
        }
        if(s_border_list && (HWND)lparam==s_border_list && HIWORD(wparam)==LBN_SELCHANGE) {
            int style=(int)SendMessageW(s_border_list,LB_GETCURSEL,0,0);
            if(style>=0 && style<s_border_style_count &&
                !border_choice_apply(border_style_to_choice(style,s_border_weave),true)) {
                int previous=border_choice_to_style(s_border_selection);
                SendMessageW(s_border_list,LB_SETCURSEL,
                    previous<0?(WPARAM)-1:(WPARAM)previous,0);
                MessageBoxW(window,L"GPU 边框纹理切换失败，保留原样式。",L"添加边框",MB_OK|MB_ICONERROR);
            }
            return 0;
        }
        return DefWindowProcW(window,msg,wparam,lparam);
    case WM_MEASUREITEM: {
        MEASUREITEMSTRUCT *m=(MEASUREITEMSTRUCT *)lparam;
        if(m->CtlID==FGS_BORDER_LIST_ID){m->itemHeight=BORDER_LIST_ITEM_H;return TRUE;}
        return DefWindowProcW(window,msg,wparam,lparam);
    }
    case WM_DRAWITEM: {
        DRAWITEMSTRUCT *d=(DRAWITEMSTRUCT *)lparam;
        if(d->CtlID!=FGS_BORDER_LIST_ID)return DefWindowProcW(window,msg,wparam,lparam);
        HBRUSH bg=CreateSolidBrush((d->itemState&ODS_SELECTED)?RGB(48,80,73):RGB(27,28,30));
        FillRect(d->hDC,&d->rcItem,bg);DeleteObject(bg);
        if(d->itemID!=(UINT)-1 && d->itemID<(UINT)s_border_style_count) {
            int index=s_border_style_indices[d->itemID];
            int x=d->rcItem.left+(d->rcItem.right-d->rcItem.left-BORDER_THUMB_W)/2;
            int y=d->rcItem.top+3;
            RECT thumb={x,y,x+BORDER_THUMB_W,y+BORDER_THUMB_H};
            HBRUSH placeholder=CreateSolidBrush(RGB(88,92,96));
            FillRect(d->hDC,&thumb,placeholder);DeleteObject(placeholder);
            HBITMAP bmp=s_border_thumb_bitmaps[d->itemID];
            if(bmp) {
                HDC source=CreateCompatibleDC(d->hDC);
                if(source) {
                    HGDIOBJ previous=SelectObject(source,bmp);
                    BitBlt(d->hDC,x,y,BORDER_THUMB_W,BORDER_THUMB_H,source,0,0,SRCCOPY);
                    SelectObject(source,previous);DeleteDC(source);
                }
            }
            HPEN stroke=CreatePen(PS_SOLID,1,(d->itemState&ODS_SELECTED)?
                RGB(0,186,139):RGB(76,80,84));
            HGDIOBJ oldpen=SelectObject(d->hDC,stroke);
            HGDIOBJ oldbrush=SelectObject(d->hDC,GetStockObject(NULL_BRUSH));
            Rectangle(d->hDC,x,y,x+BORDER_THUMB_W,y+BORDER_THUMB_H);
            SelectObject(d->hDC,oldbrush);SelectObject(d->hDC,oldpen);DeleteObject(stroke);
            wchar_t title[128];wcscpy(title,s_border_choices[index].title);
            wchar_t *suffix=wcsstr(title,L" · 静态");if(suffix)*suffix=0;
            RECT label={d->rcItem.left+4,y+BORDER_THUMB_H+3,
                        d->rcItem.right-4,d->rcItem.bottom-2};
            HFONT font=s_panel_font;
            HGDIOBJ oldfont=font?SelectObject(d->hDC,font):NULL;
            SetBkMode(d->hDC,TRANSPARENT);SetTextColor(d->hDC,RGB(235,237,240));
            DrawTextW(d->hDC,title,-1,&label,DT_CENTER|DT_VCENTER|DT_SINGLELINE|DT_END_ELLIPSIS|DT_NOPREFIX);
            if(font)SelectObject(d->hDC,oldfont);
        }
        return TRUE;
    }
    case WM_CTLCOLORLISTBOX:
        if(s_border_list && (HWND)lparam==s_border_list && s_edit_brush) {
            SetBkMode((HDC)wparam,TRANSPARENT);
            SetTextColor((HDC)wparam,RGB(235,237,240));
            SetBkColor((HDC)wparam,RGB(27,28,30));
            return (LRESULT)s_edit_brush;
        }
        return DefWindowProcW(window,msg,wparam,lparam);
    case WM_GETMINMAXINFO: {
        MINMAXINFO *limits = (MINMAXINFO *)lparam;
        limits->ptMinTrackSize.x = s_border_settings?980:820;
        limits->ptMinTrackSize.y = s_border_settings?720+border_timeline_shift():420;
        return 0;
    }
    case WM_CTLCOLOREDIT:
        if (((HWND)lparam==s_time_edit || (HWND)lparam==s_focus_x_edit ||
             (HWND)lparam==s_focus_y_edit) && s_edit_brush) {
            HDC dc=(HDC)wparam;
            SetTextColor(dc,RGB(229,240,232));
            SetBkColor(dc,RGB(27,28,30));
            return (LRESULT)s_edit_brush;
        }
        return DefWindowProcW(window,msg,wparam,lparam);
    case MSG_SEEK_FRAME:
        seek_frame_from_edit();
        return 0;
    case WM_KEYDOWN:
        if (wparam == VK_ESCAPE) { DestroyWindow(window); return 0; }
        if (wparam == VK_RETURN && s_result_path) {
            if (write_result()) DestroyWindow(window);
            else MessageBoxW(window, L"无法写入调整结果。", L"FGS GPU 预览", MB_ICONERROR|MB_OK);
            return 0;
        }
        if(s_border_settings && !s_border_list && (wparam=='B'||wparam==VK_LEFT||wparam==VK_RIGHT)) {
            int step=wparam==VK_LEFT?-1:1;
            s_border_mode=(s_border_mode+step+3)%3;
            s_border_weave=s_border_mode==2;
        } else if (wparam == 'G' && s_has_grain) {
            s_display_grain = !s_display_grain;
            if (s_display_grain && s_plate_mode && s_strength==0) s_strength=1;
        }
        else if (wparam == '1') {s_pixel_view=!s_pixel_view;s_pan_x=s_pan_y=0;}
        else if (wparam == 'L' && s_lut_source) s_lut_enabled = !s_lut_enabled;
        else if (wparam == 'J' && s_lut_source) {
            s_lut_strength = s_lut_strength >= 25 ? s_lut_strength-25 : 0;
            s_lut_enabled = true;
        } else if (wparam == 'K' && s_lut_source) {
            s_lut_strength = s_lut_strength <= 75 ? s_lut_strength+25 : 100;
            s_lut_enabled = true;
        }
        else if (s_has_grain && (wparam == VK_LEFT || wparam == VK_DOWN)) {
            s_strength = s_digital_mode ? (s_strength>15?s_strength-5:15) :
                (s_strength>=1 ? s_strength-1 : 0);
            s_display_grain = true;
        } else if (s_has_grain && (wparam == VK_RIGHT || wparam == VK_UP)) {
            s_strength = s_fgsim_mode ? (s_strength<3?s_strength+1:3) :
                s_digital_mode ? (s_strength<100?s_strength+5:100) :
                (s_strength<100 ? s_strength+1 : 100);
            s_display_grain = true;
        } else if (wparam == VK_HOME && s_has_grain) {s_strength = s_digital_mode ? 15 : s_plate_mode ? 1 : 0; s_display_grain = true;}
        else if (wparam == VK_END && s_has_grain) {s_strength = s_fgsim_mode ? 3 : 100; s_display_grain = true;}
        else return DefWindowProcW(window, msg, wparam, lparam);
        redraw_controls();
        return 0;
    case WM_ERASEBKGND: return 1;
    case WM_PAINT:
        paint_sliders(window);
        return 0;
    case WM_SIZE: {
        RECT client; GetClientRect(window, &client);
        int view_height = client.bottom-panel_height();
        if (view_height < 1) view_height = 1;
        if (s_view) MoveWindow(s_view,s_border_settings?BORDER_SIDEBAR_WIDTH:0,0,
            client.right-(s_border_settings?BORDER_SIDEBAR_WIDTH:0),view_height,TRUE);
        if(s_border_list)border_list_layout(&client);
        if(s_border_settings)focus_edit_layout(&client);
        if (s_time_edit) {
            RECT edit,random_btn,seek_btn;
            int hint_left;
            bottom_frame_controls(&client,&edit,&random_btn,&seek_btn,&hint_left);
            MoveWindow(s_time_edit,edit.left,edit.top,
                       edit.right-edit.left,edit.bottom-edit.top,TRUE);
        }
        InvalidateRect(window, NULL, FALSE);
        return 0;
    }
    case WM_DESTROY:
        for(int i=0;i<s_border_style_count;i++)
            if(s_border_thumb_bitmaps[i]) {
                DeleteObject(s_border_thumb_bitmaps[i]);s_border_thumb_bitmaps[i]=NULL;
            }
        if (s_window_shown) save_window_bounds(window);
        PostQuitMessage(0);
        return 0;
    default:
        return DefWindowProcW(window, msg, wparam, lparam);
    }
}

static void destroy_gpu(void)
{
    if (s_renderer) pl_renderer_destroy(&s_renderer);
    if (s_border_dispatch) pl_dispatch_destroy(&s_border_dispatch);
    if (s_device) {
        for (int i=0; i<3; i++) pl_tex_destroy(s_device->gpu, &s_textures[i]);
        for (int i=0; i<3; i++) pl_tex_destroy(s_device->gpu, &s_plate_textures[i]);
        pl_tex_destroy(s_device->gpu,&s_pixel_noise);
        pl_tex_destroy(s_device->gpu,&s_border_texture);
        pl_tex_destroy(s_device->gpu,&s_border_content);
        pl_tex_destroy(s_device->gpu,&s_border_composite);
    }
    if (s_swapchain) pl_swapchain_destroy(&s_swapchain);
    if (s_device) pl_d3d11_destroy(&s_device);
    for (int i=0; i<3; i++) free(s_lut_storage[i]);
    free(s_lut_dynamic_data);
    if (s_lut_source) pl_lut_free(&s_lut_source);
    if (s_log) pl_log_destroy(&s_log);
    if (s_panel_brush) DeleteObject(s_panel_brush);
    if (s_edit_brush) DeleteObject(s_edit_brush);
    if (s_panel_font) DeleteObject(s_panel_font);
    free(s_table_data);
    free(s_fgsim_shader);
}

static bool gpu_init(void)
{
    s_log = pl_log_create(PL_API_VER, pl_log_params(
        .log_cb = pl_log_simple, .log_level = PL_LOG_WARN));
    if (!s_log) return false;
    const struct pl_d3d11_params device_params = { .allow_software = false };
    s_device = pl_d3d11_create(s_log, &device_params);
    if (!s_device || s_device->software) {
        fprintf(stderr, "No hardware D3D11 GPU available\n");
        return false;
    }
    s_swapchain = pl_d3d11_create_swapchain(s_device, pl_d3d11_swapchain_params(
        .window = s_view, .disable_10bit_sdr = !s_hdr_transfer));
    if (!s_swapchain) return false;
    s_renderer = pl_renderer_create(s_log, s_device->gpu);
    return s_renderer != NULL;
}

static bool int_arg(const wchar_t *arg, int min, int max, int *value)
{
    wchar_t *end;
    long v = wcstol(arg, &end, 10);
    if (end == arg || *end || v < min || v > max) return false;
    *value = (int)v;
    return true;
}

static bool time_arg(const wchar_t *text, uint64_t *ticks)
{
    wchar_t *end;
    double seconds = wcstod(text, &end);
    if (end == text || *end || !isfinite(seconds) || seconds < 0 || seconds > 86400)
        return false;
    *ticks = (uint64_t)llround(seconds*10000000.0);
    return true;
}

static bool frame_rate_arg(const wchar_t *text, uint32_t *num, uint32_t *den)
{
    unsigned long numerator=0, denominator=1;
    wchar_t extra;
    if (wcschr(text,L'/')) {
        if (swscanf(text,L"%lu/%lu%lc",&numerator,&denominator,&extra)!=2) return false;
    } else {
        wchar_t *end;
        double fps=wcstod(text,&end);
        if (end==text || *end || !isfinite(fps) || fps<1.0 || fps>240.0) return false;
        numerator=(unsigned long)llround(fps*1000.0);
        denominator=1000;
    }
    if (!numerator || numerator>1000000 || !denominator || denominator>1000000 ||
        (double)numerator/denominator<1.0 || (double)numerator/denominator>240.0)
        return false;
    *num=(uint32_t)numerator;*den=(uint32_t)denominator;
    return true;
}

static int compare_latency(const void *left, const void *right)
{
    double a = *(const double *)left, b = *(const double *)right;
    return (a > b) - (a < b);
}

/* Offscreen audit uses D3D11 WARP on a runner without a physical GPU. */
static int audit_film_border(void)
{
    enum { width = 64, height = 64 };
    s_width=width;
    s_height=height;
    s_log=pl_log_create(PL_API_VER,pl_log_params(.log_cb=pl_log_simple,.log_level=PL_LOG_WARN));
    if (!s_log) return 8;
    s_device=pl_d3d11_create(s_log,&(struct pl_d3d11_params){.allow_software=true,.force_software=true});
    if (!s_device) {destroy_gpu();return 8;}
    pl_gpu gpu=s_device->gpu;
    s_renderer=pl_renderer_create(s_log,gpu);
    unsigned char yuv[width*height*3/2], rgba[width*height*4];
    memset(yuv,200,width*height);
    memset(yuv+width*height,128,width*height/2);
    const unsigned char transparent[16]={0};
    pl_fmt format=pl_find_fmt(gpu,PL_FMT_UNORM,4,8,8,
                             PL_FMT_CAP_SAMPLEABLE | PL_FMT_CAP_LINEAR | PL_FMT_CAP_RENDERABLE | PL_FMT_CAP_HOST_READABLE);
    pl_tex target = format ? pl_tex_create(gpu,&(struct pl_tex_params){
        .w=width,.h=height,.format=format,.renderable=true,.host_readable=true,
    }) : NULL;
    s_border_texture = format ? pl_tex_create(gpu,&(struct pl_tex_params){
        .w=2,.h=2,.format=format,.sampleable=true,.initial_data=transparent,
    }) : NULL;
    bool okay=s_renderer && target && s_border_texture && init_film_border_targets() && upload_frame(yuv,8);
    if (okay) {
        struct pl_frame output={
            .num_planes=1,.planes={{.texture=target,.components=4,.component_mapping={0,1,2,3}}},
            .repr=pl_color_repr_rgb,.color=s_image.color,.crop=s_image.crop,
        };
        okay=render_film_border(s_image,&output,&pl_render_default_params) &&
             pl_tex_download(gpu,&(struct pl_tex_transfer_params){.tex=target,.ptr=rgba});
        const size_t middle=(height/2*width+width/2)*4;
        if (okay) okay=rgba[0]<12 && rgba[1]<12 && rgba[2]<12 &&
            rgba[middle]>160 && rgba[middle+1]>160 && rgba[middle+2]>160;
        if (okay) {
            s_initial_border_mode=L"CLASSIC_STATIC";
            okay=render_film_border(s_image,&output,&pl_render_default_params) &&
                 pl_tex_download(gpu,&(struct pl_tex_transfer_params){.tex=target,.ptr=rgba});
            size_t top=(5*width+width/2)*4;
            if (okay)okay=rgba[top]<12 && rgba[top+1]<12 && rgba[top+2]<12 &&
                          rgba[middle]>160 && rgba[middle+1]>160 && rgba[middle+2]>160;
            s_initial_border_mode=NULL;
        }
        if (!okay) fprintf(stderr,"Film border pixel audit failed: dark/classic geometry or source aperture\n");
    }
    pl_tex_destroy(gpu,&target);
    destroy_gpu();
    if (okay) puts("PASS D3D11 WARP: black frame edge and intact source in the aperture");
    return okay ? 0 : 8;
}

static int audit_border_framing(void)
{
    const int sizes[][4]={{1920,1080,1680,990},{1920,1080,1680,748},
        {3840,2160,3360,1496},{1920,1080,1630,698},{1920,1080,1730,798},
        {1080,1920,950,700}};
    const double foci[]={0,0.25,0.5,0.75,1};
    const double zooms[]={1,1.5,2,3};
    for(size_t k=0;k<sizeof(sizes)/sizeof(sizes[0]);k++)
    for(size_t zi=0;zi<sizeof(zooms)/sizeof(zooms[0]);zi++)
    for(size_t fx=0;fx<sizeof(foci)/sizeof(foci[0]);fx++)
    for(size_t fy=0;fy<sizeof(foci)/sizeof(foci[0]);fy++) {
        int w=sizes[k][0],h=sizes[k][1],aw=sizes[k][2],ah=sizes[k][3];
        struct fgs_focus_rect crop=fgs_focus_crop(w,h,aw,ah,foci[fx],foci[fy],zooms[zi]);
        if(crop.width<=0||crop.height<=0||crop.left<-.00001||crop.top<-.00001||
           crop.left+crop.width>w+.00001||crop.top+crop.height>h+.00001)
            return 12;
        if(foci[fx]==0.5&&foci[fy]==0.5&&zooms[zi]==1 &&
           (fabs(crop.left-(w-crop.width)/2)>1e-8||
            fabs(crop.top-(h-crop.height)/2)>1e-8))return 13;
    }
    /* A centered free crop preserves the pixel under the cursor as zoom changes. */
    double x=.5,y=.5,z=1.7;
    const double u=.35,v=.65;
    struct fgs_focus_rect a=fgs_focus_crop(1920,1080,1680,748,x,y,z);
    const double before_x=a.left+u*a.width,before_y=a.top+v*a.height;
    fgs_focus_zoom_at(1920,1080,1680,748,u,v,2.1,&x,&y,&z);
    struct fgs_focus_rect b=fgs_focus_crop(1920,1080,1680,748,x,y,z);
    if(fabs(before_x-(b.left+u*b.width))>1e-6||
       fabs(before_y-(b.top+v*b.height))>1e-6)return 14;
    /* The X/Y row must stay centered at any window width and use the time-field height. */
    const int widths[]={820,1280,1800};
    for(size_t i=0;i<sizeof(widths)/sizeof(widths[0]);i++) {
        RECT client={0,0,widths[i],900},lx,ex,ly,ey;
        focus_edit_rects(&client,&lx,&ex,&ly,&ey);
        if(lx.left+ey.right!=client.right || ex.top!=ey.top ||
           ex.bottom-ex.top!=s_time_edit_height || ey.bottom-ey.top!=s_time_edit_height)
            return 15;
    }
    // Assert the relocated toolbar sits between the timeline slider and the style selector.
    s_border_settings=true;
    s_video_path=L"a.mkv";
    if(panel_height()!=BORDER_PANEL_HEIGHT+BORDER_TIMELINE_SHIFT)return 16;
    RECT client={0,0,1280,960},time_edit,random_btn,seek_btn,import_btn,remove_btn,edit_btn,rebuild_btn;
    int hint_left;
    bottom_frame_controls(&client,&time_edit,&random_btn,&seek_btn,&hint_left);
    border_choice_buttons(&client,&import_btn,&remove_btn,&edit_btn,&rebuild_btn);
    int panel_top=client.bottom-panel_height();
    if(time_edit.top<panel_top+66 || random_btn.top<panel_top+66 ||
       seek_btn.bottom>=import_btn.top || time_edit.left+seek_btn.right!=client.right)
        return 17;
    s_video_path=NULL;s_border_settings=false;
    puts("PASS Film Border focus/zoom: neutral, corners, centered X/Y, toolbar under frame slider");
    return 0;
}

/* CPU-only gallery contract audit; no libplacebo device or Windows UI required. */
static int audit_border_thumbnails(void)
{
    const char *modes[]={"OFF","DARK_STATIC","DARK_WEAVE",
        "CLASSIC_STATIC","CLASSIC_WEAVE",
        "USER_0123456789ABCDEF0123456789ABCDEF_STATIC",
        "USER_0123456789ABCDEF0123456789ABCDEF_WEAVE"};
    s_border_choices_count=7;s_border_style_count=3;
    for(int i=0;i<7;i++)strcpy(s_border_choices[i].mode,modes[i]);
    s_border_style_indices[0]=1;s_border_style_indices[1]=3;s_border_style_indices[2]=5;
    for(int style=0;style<3;style++)for(int dynamic=0;dynamic<=1;dynamic++) {
        int index=border_style_to_choice(style,dynamic!=0);
        if(index!=1+style*2+dynamic || border_choice_to_style(index)!=style)return 81;
    }
    if(border_choice_to_style(0)!=-1)return 82;
    RECT client={0,0,1280,960},import,remove,edit,rebuild,stat,weave,off,up,down;
    s_border_settings=true;s_video_path=L"test.mkv";
    border_choice_buttons(&client,&import,&remove,&edit,&rebuild);
    border_mode_buttons(&client,&stat,&weave,&off);
    border_sidebar_buttons(&client,&up,&down);
    POINT up_point={(up.left+up.right)/2,(up.top+up.bottom)/2};
    POINT down_point={(down.left+down.right)/2,(down.top+down.bottom)/2};
    if(border_sidebar_hit(&client,up_point)!=-1 ||
       border_sidebar_hit(&client,down_point)!=1)return 84;
    if(!(off.right<rebuild.left && rebuild.right<import.left &&
         import.right<remove.left && remove.right<edit.left &&
         up.bottom<BORDER_LIST_TOP && down.top>up.bottom &&
         down.bottom<=client.bottom-panel_height()))return 83;
    s_border_settings=false;s_video_path=NULL;
    puts("PASS Film Border gallery: 3 styles / 6 modes, OFF, toolbar, sidebar arrows");
    return 0;
}

int wmain(int argc, wchar_t **argv)
{
    QueryPerformanceFrequency(&s_frequency);
    if (argc == 2 && !wcscmp(argv[1], L"--audit-border-thumbnails")) return audit_border_thumbnails();
    if (argc == 2 && !wcscmp(argv[1], L"--audit-border-framing")) return audit_border_framing();
    if (argc == 2 && !wcscmp(argv[1], L"--audit-film-border")) return audit_film_border();
    if (argc == 2 && !wcscmp(argv[1], L"--audit-border-modes")) {
        const wchar_t *valid[]={L"OFF",L"DARK_STATIC",L"DARK_WEAVE",L"CLASSIC_STATIC",
            L"CLASSIC_WEAVE",L"USER_0123456789ABCDEF0123456789ABCDEF_STATIC",
            L"USER_0123456789ABCDEF0123456789ABCDEF_WEAVE"};
        const wchar_t *invalid[]={L"CLASSIC",L"DARK_SIDEWAYS",L"_STATIC",L"classic_weave"};
        for(size_t i=0;i<sizeof(valid)/sizeof(valid[0]);i++)
            if(!valid_border_mode_arg(valid[i]))return 9;
        for(size_t i=0;i<sizeof(invalid)/sizeof(invalid[0]);i++)
            if(valid_border_mode_arg(invalid[i]))return 9;
        puts("PASS Film Border catalog mode argument validation");
        return 0;
    }
    double start = timer_ms();
    if (argc < 6) {
        fwprintf(stderr, L"Usage: %ls FRAME.yuv WIDTH HEIGHT FILMGRN1.tbl|- 8|10 [BENCH_COUNT] [MODEL_TIME_SEC] [LUT.cube] [--fps NUM/DEN --result FILE --grain 0..100 --lut 0..100 --grain-on 0|1 --lut-on 0|1 --video FILE --ffmpeg EXE --duration SECONDS --plate MOV --plate-frame YUV --plate-cycle SECONDS --source-color pq|hlg|pq-full|hlg-full --deinterlace auto|tff|bff]\n", argv[0]);
        return 2;
    }
    int depth, bench_count = 0;
    uint64_t timeline_ticks = 0;
    const wchar_t *lut_path = NULL;
    bool grain_on = true, lut_on = true;
    int next = 6, flag;
    if (next < argc && wcsncmp(argv[next], L"--", 2)) {
        if (!int_arg(argv[next++], 0, 2000, &bench_count)) return 2;
    }
    if (next < argc && wcsncmp(argv[next], L"--", 2)) {
        if (!time_arg(argv[next++], &timeline_ticks)) return 2;
    }
    if (next < argc && wcsncmp(argv[next], L"--", 2)) lut_path = argv[next++];
    while (next < argc) {
        if (next + 1 >= argc) return 2;
        const wchar_t *key = argv[next], *value = argv[next+1];
        if (!wcscmp(key, L"--result")) s_result_path = value;
        else if (!wcscmp(key, L"--combined-grade")) {if (!int_arg(value, 0, 1, &flag)) return 2;s_combined_grade = flag != 0;}
        else if (!wcscmp(key, L"--hint-en")) {if (!int_arg(value, 0, 1, &flag)) return 2;s_hint_en = flag != 0;}
        else if (!wcscmp(key, L"--grain")) {if (!int_arg(value, 0, 100, &s_strength)) return 2;}
        else if (!wcscmp(key, L"--lut")) {if (!int_arg(value, 0, 100, &s_lut_strength)) return 2;}
        else if (!wcscmp(key, L"--grain-on")) {if (!int_arg(value, 0, 1, &flag)) return 2;grain_on = flag != 0;}
        else if (!wcscmp(key, L"--lut-on")) {if (!int_arg(value, 0, 1, &flag)) return 2;lut_on = flag != 0;}
        else if (!wcscmp(key, L"--video")) s_video_path = value;
        else if (!wcscmp(key, L"--ffmpeg")) s_ffmpeg_path = value;
        else if (!wcscmp(key,L"--pixel-mode")) {
            if (!wcscmp(value,L"FGSIM")) s_fgsim_mode=true;
            else if (!wcscmp(value,L"DIGITAL")) s_digital_mode=true;
            else return 2;
        }
        else if (!wcscmp(key,L"--fgsim-noise")) s_fgsim_noise_path=value;
        else if (!wcscmp(key,L"--fgsim-shader")) s_fgsim_shader_path=value;
        else if (!wcscmp(key,L"--digital-noise")) s_digital_path=value;
        else if (!wcscmp(key,L"--noise-ffmpeg")) s_noise_ffmpeg=value;
        else if (!wcscmp(key,L"--film-focus-x") || !wcscmp(key,L"--film-focus-y") || !wcscmp(key,L"--film-zoom")) {
            wchar_t *end=NULL;
            double n=wcstod(value,&end);
            double low=!wcscmp(key,L"--film-zoom")?1.0:0.0;
            double high=!wcscmp(key,L"--film-zoom")?3.0:1.0;
            if(end==value||*end||!isfinite(n)||n<low||n>high)return 2;
            if(!wcscmp(key,L"--film-focus-x"))s_focus_x=n;
            else if(!wcscmp(key,L"--film-focus-y"))s_focus_y=n;
            else s_focus_zoom=n;
        }
        else if (!wcscmp(key, L"--film-border")) s_border_path = value;
        else if (!wcscmp(key, L"--film-border-settings")) {
            if(!int_arg(value,0,1,&flag))return 2;
            s_border_settings=flag!=0;
        }
        else if (!wcscmp(key, L"--border-choices-file")) s_border_choices_file=value;
        else if (!wcscmp(key,L"--film-border-aperture-w")) {
            if(!int_arg(value,2,1920,&s_preview_aperture_width))return 2;
        }
        else if (!wcscmp(key,L"--film-border-aperture-h")) {
            if(!int_arg(value,2,1080,&s_preview_aperture_height))return 2;
        }
        else if (!wcscmp(key,L"--film-motion-amp") ||
                 !wcscmp(key,L"--film-motion-speed") ||
                 !wcscmp(key,L"--film-motion-frequency")) {
            wchar_t *end=NULL;
            double x=wcstod(value,&end);
            double min=!wcscmp(key,L"--film-motion-amp")?0.0:0.25;
            if(end==value||*end||!isfinite(x)||x<min||x>3.0)return 2;
            if(!wcscmp(key,L"--film-motion-amp"))s_motion_amp=x;
            else if(!wcscmp(key,L"--film-motion-speed"))s_motion_speed=x;
            else s_motion_frequency=x;
        }
        else if (!wcscmp(key, L"--film-border-mode")) {
            if(!wcscmp(value,L"OFF"))s_border_mode=0;
            else if(!wcscmp(value,L"DARK_STATIC"))s_border_mode=1;
            else if(!wcscmp(value,L"DARK_WEAVE"))s_border_mode=2;
            else if(!valid_border_mode_arg(value))return 2;
            s_border_weave=(wcslen(value)>=6 &&
                !wcscmp(value+wcslen(value)-6,L"_WEAVE"));
            s_initial_border_mode=value;
        }
        else if (!wcscmp(key, L"--film-weave")) {if (!int_arg(value,0,1,&flag)) return 2;s_border_weave=flag!=0;}
        else if (!wcscmp(key, L"--plate")) s_plate_path = value;
        else if (!wcscmp(key, L"--plate-frame")) s_plate_frame_path = value;
        else if (!wcscmp(key, L"--plate-cycle")) {
            uint64_t ticks;
            if (!time_arg(value,&ticks) || ticks==0) return 2;
            s_plate_cycle_seconds=(double)ticks/10000000.0;
        }
        else if (!wcscmp(key, L"--source-color")) {
            if (!wcscmp(value,L"pq") || !wcscmp(value,L"pq-full")) s_hdr_transfer = PL_COLOR_TRC_PQ;
            else if (!wcscmp(value,L"hlg") || !wcscmp(value,L"hlg-full")) s_hdr_transfer = PL_COLOR_TRC_HLG;
            else return 2;
            s_source_full = wcsstr(value,L"-full") != NULL;
        }
        else if (!wcscmp(key, L"--deinterlace")) {
            if (!wcscmp(value,L"auto")) s_deinterlace = L"bwdif=mode=send_frame:parity=auto:deint=all,";
            else if (!wcscmp(value,L"tff")) s_deinterlace = L"bwdif=mode=send_frame:parity=tff:deint=all,";
            else if (!wcscmp(value,L"bff")) s_deinterlace = L"bwdif=mode=send_frame:parity=bff:deint=all,";
            else return 2;
        }
        else if (!wcscmp(key, L"--fps")) {if (!frame_rate_arg(value,&s_fps_num,&s_fps_den)) return 2;}
        else if (!wcscmp(key, L"--duration")) {
            uint64_t duration_ticks;
            if (!time_arg(value,&duration_ticks)) return 2;
            s_duration_seconds=(double)duration_ticks/10000000.0;
        }
        else {fwprintf(stderr, L"Unknown option: %ls\n", key);return 2;}
        next += 2;
    }
    if (s_result_path && !*s_result_path) return 2;
    if (s_border_settings && (!s_result_path || !s_border_path)) return 2;
    if (!!s_video_path != !!s_ffmpeg_path || (s_video_path && s_duration_seconds<=0)) {
        fprintf(stderr,"Frame selection requires --video, --ffmpeg and positive --duration\n");
        return 2;
    }
    if (!!s_plate_path != !!s_plate_frame_path ||
        (s_plate_path && (!s_ffmpeg_path || !s_video_path || wcscmp(argv[4],L"-") != 0))) {
        fprintf(stderr,"Grain Plate requires --plate/--plate-frame, --video/--ffmpeg and no AV1 grain table\n");
        return 2;
    }
    if (s_plate_cycle_seconds>0 && !s_plate_path) return 2;
    s_plate_mode = s_plate_path != NULL;
    bool pixel_mode=s_fgsim_mode || s_digital_mode;
    if ((s_fgsim_mode && s_digital_mode) ||
        (pixel_mode && (s_plate_mode || wcscmp(argv[4],L"-") || bench_count)) ||
        (s_fgsim_mode && (!s_fgsim_noise_path || !s_fgsim_shader_path || s_strength>3)) ||
        (s_digital_mode && (!s_digital_path || !s_noise_ffmpeg || s_strength<15 || s_strength%5))) return 2;
    s_has_grain = pixel_mode || s_plate_mode || wcscmp(argv[4], L"-") != 0;
    s_display_grain = s_has_grain && grain_on;
    if (!int_arg(argv[2], 2, 8192, &s_width) ||
        !int_arg(argv[3], 2, 8192, &s_height) ||
        !int_arg(argv[5], 8, 10, &depth) || (depth != 8 && depth != 10) ||
        (s_width & 1) || (s_height & 1) || (s_hdr_transfer && depth != 10) ||
        (s_strength < 0 || s_strength > 100) || (s_plate_mode && depth != 10)) {
        fprintf(stderr, "Invalid even dimensions, bit depth, bench count or timeline seconds\n");
        return 2;
    }
    if(s_border_settings && s_border_choices_file &&
       !load_border_choices(s_border_choices_file,s_initial_border_mode?
             s_initial_border_mode:L"OFF")) {
        fprintf(stderr,"Film border choices file is invalid\n");return 2;
    }
    if(s_border_choices_count) {
        s_border_path=s_border_choices[s_border_selection].raw_path;
        s_border_weave=s_border_choices[s_border_selection].weave;
    }
    s_frame_path=argv[1];s_frame_depth=depth;
    s_frame_seconds=s_pending_seconds=(double)timeline_ticks/10000000.0;
    if (s_duration_seconds>0 && s_frame_seconds>s_duration_seconds) return 2;
    srand((unsigned)GetTickCount());
    unsigned char *table = NULL, *raw = NULL, *plate_raw = NULL;
    size_t bytes_per_sample = depth == 8 ? 1 : 2;
    size_t frame_bytes = (size_t)s_width * s_height * 3 / 2 * bytes_per_sample;
    char reason[256];
    bool okay = true;
    if (s_has_grain && !s_plate_mode && !pixel_mode) {
        if (!read_file(argv[4], 0, 4*1024*1024, &table, NULL)) return 3;
        okay = fgs_parse_table_at_event((const char *)table, timeline_ticks,
                                        &s_original,&s_grain_event_start,reason,sizeof(reason));
    }
    if (!okay) { fprintf(stderr, "Unsupported grain table: %s\n", reason); free(table);return 3; }
    s_table_data=(char*)table;
    if (!read_file(argv[1], frame_bytes, frame_bytes, &raw, NULL)) return 4;
    if (s_plate_mode && !read_file(s_plate_frame_path,frame_bytes,frame_bytes,&plate_raw,NULL)) {
        free(raw);return 4;
    }
    s_plate_hook = (struct pl_hook){
        .stages=PL_HOOK_NATIVE,.input=PL_HOOK_SIG_COLOR,
        .hook=plate_overlay_hook,.signature=UINT64_C(0x464753504c415445),
    };

    s_pixel_hook = (struct pl_hook){
        .stages=PL_HOOK_RGB,.input=PL_HOOK_SIG_COLOR,.hook=pixel_grain_hook,
        .signature=s_fgsim_mode?UINT64_C(0x464753494d):UINT64_C(0x4647534447),
    };

    HINSTANCE instance = GetModuleHandleW(NULL);
    WNDCLASSW wc = {0};
    wc.lpfnWndProc = window_proc;
    wc.hInstance = instance;
    wc.hCursor = LoadCursorW(NULL, IDC_ARROW);
    wc.hIcon = LoadIconW(instance, MAKEINTRESOURCEW(101));
    wc.lpszClassName = L"FgsGpuPreviewLab";
    if (!RegisterClassW(&wc)) {free(raw);free(plate_raw);return 5;}
    WNDCLASSW view_class = wc;
    view_class.style = CS_DBLCLKS;
    view_class.lpfnWndProc = view_proc;
    view_class.lpszClassName = L"FgsGpuViewLab";
    if (!RegisterClassW(&view_class)) {free(raw);free(plate_raw);return 5;}
    s_panel_brush = CreateSolidBrush(RGB(37,38,40));
    if (!s_panel_brush) {free(raw);free(plate_raw);return 5;}
    s_edit_brush = CreateSolidBrush(RGB(27,28,30));
    HDC font_dc=GetDC(NULL);
    int label_height=-MulDiv(9,GetDeviceCaps(font_dc,LOGPIXELSY),72);
    s_panel_font = CreateFontW(label_height, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
                              DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                              CLEARTYPE_QUALITY, DEFAULT_PITCH|FF_SWISS, L"Segoe UI");
    if (s_panel_font) {
        HGDIOBJ previous=SelectObject(font_dc,s_panel_font);
        const wchar_t *sample=L"00:00:00.000";
        SIZE size;
        TEXTMETRICW metrics;
        if (GetTextExtentPoint32W(font_dc,sample,(int)wcslen(sample),&size))
            s_time_edit_width=size.cx+16;
        if (GetTextMetricsW(font_dc,&metrics))
            s_time_edit_height=metrics.tmHeight+6;
        SelectObject(font_dc,previous);
    }
    ReleaseDC(NULL,font_dc);
    s_window = CreateWindowExW(0, wc.lpszClassName, s_border_settings?L"FGS 添加边框":L"FGS GPU Preview",
                WS_OVERLAPPEDWINDOW|WS_CLIPCHILDREN,
                CW_USEDEFAULT, CW_USEDEFAULT,
                s_width < 1280 ? (s_width+32 < (s_border_settings?980:820) ?
                    (s_border_settings?980:820) : s_width+32) : 1280,
                s_height < 720 ? s_height+panel_height()+58 : 880+border_timeline_shift(),
                NULL, NULL, instance, NULL);
    if (!s_window) {free(raw);free(plate_raw);return 5;}
    SendMessageW(s_window, WM_SETICON, ICON_SMALL, (LPARAM)LoadImageW(instance,
        MAKEINTRESOURCEW(101), IMAGE_ICON, GetSystemMetrics(SM_CXSMICON),
        GetSystemMetrics(SM_CYSMICON), LR_SHARED));
    RECT client; GetClientRect(s_window, &client);
    if (s_video_path) {
        RECT edit,random_btn,seek_btn;
        int hint_left;
        bottom_frame_controls(&client,&edit,&random_btn,&seek_btn,&hint_left);
        s_time_edit=CreateWindowExW(0,L"EDIT",L"",WS_CHILD|WS_VISIBLE|WS_BORDER|
                    WS_TABSTOP|ES_CENTER|ES_AUTOHSCROLL,edit.left,edit.top,
                    edit.right-edit.left,edit.bottom-edit.top,
                    s_window,NULL,instance,NULL);
        if (!s_time_edit) {free(raw);free(plate_raw);DestroyWindow(s_window);return 5;}
        if (s_panel_font) SendMessageW(s_time_edit,WM_SETFONT,(WPARAM)s_panel_font,TRUE);
        s_original_edit_proc=(WNDPROC)SetWindowLongPtrW(s_time_edit,GWLP_WNDPROC,(LONG_PTR)frame_edit_proc);
        update_frame_time_text(s_frame_seconds);
    }
    if(s_border_settings && s_border_style_count) {
        int list_height=client.bottom-panel_height()-BORDER_LIST_TOP-BORDER_LIST_FOOT;
        if(list_height<1)list_height=1;
        s_border_list=CreateWindowExW(0,L"LISTBOX",L"",
            WS_CHILD|WS_VISIBLE|WS_TABSTOP|
            LBS_NOTIFY|LBS_OWNERDRAWFIXED|LBS_HASSTRINGS|LBS_NOINTEGRALHEIGHT,
            8,BORDER_LIST_TOP,BORDER_SIDEBAR_WIDTH-16,list_height,
            s_window,(HMENU)(INT_PTR)FGS_BORDER_LIST_ID,instance,NULL);
        if(!s_border_list){free(raw);free(plate_raw);DestroyWindow(s_window);return 5;}
        if(s_panel_font)SendMessageW(s_border_list,WM_SETFONT,(WPARAM)s_panel_font,TRUE);
        for(int i=0;i<s_border_style_count;i++) {
            const struct FgsBorderChoice *c=&s_border_choices[s_border_style_indices[i]];
            SendMessageW(s_border_list,LB_ADDSTRING,0,(LPARAM)c->title);
            if(c->thumbnail_path[0])
                s_border_thumb_bitmaps[i]=(HBITMAP)LoadImageW(NULL,c->thumbnail_path,
                    IMAGE_BITMAP,0,0,LR_LOADFROMFILE|LR_CREATEDIBSECTION);
        }
        int chosen=border_choice_to_style(s_border_selection);
        SendMessageW(s_border_list,LB_SETCURSEL,chosen<0?(WPARAM)-1:(WPARAM)chosen,0);
        if(chosen>=0)SendMessageW(s_border_list,LB_SETTOPINDEX,chosen,0);
        s_original_border_list_proc=(WNDPROC)SetWindowLongPtrW(s_border_list,GWLP_WNDPROC,
            (LONG_PTR)border_list_proc);
        if(!s_original_border_list_proc){free(raw);free(plate_raw);DestroyWindow(s_window);return 5;}
    }
    if(s_border_settings && s_border_choices_count) {
        RECT label_x,edit_x,label_y,edit_y;
        focus_edit_rects(&client,&label_x,&edit_x,&label_y,&edit_y);
        s_focus_x_edit=CreateWindowExW(0,L"EDIT",L"",WS_CHILD|WS_VISIBLE|WS_BORDER|WS_TABSTOP|ES_CENTER|ES_AUTOHSCROLL,
            edit_x.left,edit_x.top,edit_x.right-edit_x.left,edit_x.bottom-edit_x.top,
            s_window,(HMENU)(INT_PTR)302,instance,NULL);
        s_focus_y_edit=CreateWindowExW(0,L"EDIT",L"",WS_CHILD|WS_VISIBLE|WS_BORDER|WS_TABSTOP|ES_CENTER|ES_AUTOHSCROLL,
            edit_y.left,edit_y.top,edit_y.right-edit_y.left,edit_y.bottom-edit_y.top,
            s_window,(HMENU)(INT_PTR)303,instance,NULL);
        if(!s_focus_x_edit||!s_focus_y_edit){free(raw);free(plate_raw);DestroyWindow(s_window);return 5;}
        if(s_panel_font) {
            SendMessageW(s_focus_x_edit,WM_SETFONT,(WPARAM)s_panel_font,TRUE);
            SendMessageW(s_focus_y_edit,WM_SETFONT,(WPARAM)s_panel_font,TRUE);
        }
        s_original_focus_edit_proc=(WNDPROC)SetWindowLongPtrW(s_focus_x_edit,GWLP_WNDPROC,(LONG_PTR)focus_edit_proc);
        SetWindowLongPtrW(s_focus_y_edit,GWLP_WNDPROC,(LONG_PTR)focus_edit_proc);
        sync_focus_edits();
    }
    int view_height = client.bottom-panel_height();
    if (view_height < 1) view_height = 1;
    s_view = CreateWindowExW(0, view_class.lpszClassName, NULL,
                WS_CHILD|WS_VISIBLE|WS_CLIPSIBLINGS,
                s_border_settings?BORDER_SIDEBAR_WIDTH:0, 0,
                client.right-(s_border_settings?BORDER_SIDEBAR_WIDTH:0), view_height,
                s_window, NULL, instance, NULL);
    if (!s_view) {free(raw);free(plate_raw);DestroyWindow(s_window);return 5;}

    int result = 0;
    if (!gpu_init() || (lut_path && !load_lut(lut_path)) || !upload_frame(raw, depth) ||
        (s_plate_mode && !upload_plate(plate_raw))) {
        fprintf(stderr, "GPU initialization/upload failed\n");
        result = 6;
        goto done;
    }
    if (s_fgsim_mode) {
        unsigned char *noise=NULL,*shader=NULL;
        bool ready=read_file(s_fgsim_shader_path,0,64*1024,&shader,NULL) &&
            read_file(s_fgsim_noise_path,1048576,1048576,&noise,NULL) &&
            upload_pixel_noise(noise,true);
        free(noise);
        s_fgsim_shader=(char*)shader;
        if (!ready) {fprintf(stderr,"FGSIM shader/texture initialization failed\n");result=6;goto done;}
    }
    if (s_digital_mode && !load_digital_noise(s_frame_seconds)) {
        fprintf(stderr,"Digital Grain noise initialization failed\n");result=6;goto done;
    }
    if (s_border_path && (!init_film_border_targets() || !upload_border_texture())) {
        fprintf(stderr,"Film border GPU texture initialization failed\n");result=6;goto done;
    }
    free(raw); raw = NULL; /* One CPU -> GPU upload, no re-upload per adjustment. */
    free(plate_raw);plate_raw=NULL;
    s_lut_enabled = s_lut_source != NULL && lut_on;
    s_setup_ms = timer_ms()-start;
    printf("phase,strength,elapsed_ms,working_mb,cpu_total_ms\n");
    printf("SETUP,100,%.3f,%.1f,%.1f\n", s_setup_ms, process_memory_mb(), process_cpu_ms());
    restore_window_bounds(s_window);
    s_window_shown = true;
    ShowWindow(s_window, s_restore_maximized ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL);
    UpdateWindow(s_window);
    UpdateWindow(s_view); /* FIRST includes initial shader compilation and presentation. */

    if (bench_count) {
        int starting_grain = s_strength, starting_lut = s_lut_strength;
        bool starting_display_grain = s_display_grain, starting_display_lut = s_lut_enabled;
        double *times = calloc((size_t)bench_count, sizeof(double));
        if (!times) { result = 7; goto done; }
        s_benchmark = true;
        for (int i=0; i<bench_count; i++) {
            s_strength = s_plate_mode ? 15+((i*7)%18)*5 : 1+(i*37)%100;
            times[i] = render_once(false);
            if (times[i] < 0) { result = 7; break; }
        }
        if (!result) {
            double total = 0, maximum = 0;
            for (int i=0; i<bench_count; i++) {
                total += times[i];
                if (times[i] > maximum) maximum = times[i];
            }
            printf("BENCH_MEAN,%d,%.3f,%.1f,%.1f\n", bench_count, total/bench_count,
                   process_memory_mb(), process_cpu_ms());
            printf("BENCH_MAX,%d,%.3f,%.1f,%.1f\n", bench_count, maximum,
                   process_memory_mb(), process_cpu_ms());
            qsort(times, (size_t)bench_count, sizeof(double), compare_latency);
            int p95 = (95*bench_count+99)/100-1;
            printf("BENCH_P95,%d,%.3f,%.1f,%.1f\n", bench_count, times[p95],
                   process_memory_mb(), process_cpu_ms());
        }
        if (!result && s_lut_source) {
            /* 1%-precision LUT changes: includes CPU LUT rebuild, shader state,
             * texture update and GPU completion. No YUV plane re-upload. */
            double total = 0, maximum = 0;
            for (int i=0; i<bench_count; i++) {
                s_lut_strength = 1+(i*37)%99;
                times[i] = render_once(false);
                if (times[i] < 0) { result = 7; break; }
                total += times[i];
                if (times[i] > maximum) maximum = times[i];
            }
            if (!result) {
                printf("LUT_BENCH_MEAN,%d,%.3f,%.1f,%.1f\n", bench_count,
                       total/bench_count, process_memory_mb(), process_cpu_ms());
                printf("LUT_BENCH_MAX,%d,%.3f,%.1f,%.1f\n", bench_count,
                       maximum, process_memory_mb(), process_cpu_ms());
                qsort(times, (size_t)bench_count, sizeof(double), compare_latency);
                int p95 = (95*bench_count+99)/100-1;
                printf("LUT_BENCH_P95,%d,%.3f,%.1f,%.1f\n", bench_count,
                       times[p95], process_memory_mb(), process_cpu_ms());
            }
        }
        free(times);
        s_strength = starting_grain;
        s_lut_strength = starting_lut;
        s_display_grain = starting_display_grain;
        s_lut_enabled = starting_display_lut;
        s_benchmark = false;
        redraw_controls();
    }

    if (!result) {
        MSG msg;
        int message_status;
        while ((message_status = GetMessageW(&msg, NULL, 0, 0)) > 0) {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        result = message_status < 0 ? 7 : (int)msg.wParam;
    }
done:
    free(plate_raw);
    free(raw);
    destroy_gpu();
    if (s_window && IsWindow(s_window)) DestroyWindow(s_window);
    return result;
}
