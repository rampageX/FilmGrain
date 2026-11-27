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
static int s_expand_px;
static double s_expand_sar=1.0;
static int s_expand_left,s_expand_top,s_expand_right,s_expand_bottom;
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
static bool resize_expand_target(void);
static bool update_expand_geometry(void);
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
        if(!update_expand_geometry() || !upload_border_texture() || (s_expand_px && !resize_expand_target())) {
            s_border_selection=old_index;s_border_weave=old_weave;s_border_path=old;
            update_expand_geometry();
            if(s_device && s_device->gpu){upload_border_texture();if(s_expand_px)resize_expand_target();}
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
        for (int z=0; z<b; z++) for (int y=0; y…18386 tokens truncated… {
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
    if(argc==2 && !wcscmp(argv[1],L"--audit-expand-geometry"))
    {
        int l,t,r,b;
        if(!natural_expand_geometry(1920,1080,1684,970,&l,&t,&r,&b) || l!=132 || r!=132 || t!=62 || b!=62)return 9;
        if(!natural_expand_geometry(1920,1080,1888,800,&l,&t,&r,&b) || l!=16 || r!=16 || t!=142 || b!=142)return 9;
        if(!natural_expand_geometry(320,180,1680,990,&l,&t,&r,&b) || l!=22 || r!=22 || t!=8 || b!=8)return 9;
        puts("PASS natural Expand Canvas geometry");return 0;
    }
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
        else if (!wcscmp(key, L"--film-expand-px")) {
            if(!int_arg(value,0,512,&s_expand_px) || (s_expand_px&1))return 2;
        }
        else if (!wcscmp(key, L"--film-expand-sar")) {
            wchar_t *end=NULL;s_expand_sar=wcstod(value,&end);
            if(end==value||*end||!isfinite(s_expand_sar)||s_expand_sar<=0)return 2;
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
    if(!update_expand_geometry())return 2;
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
        if(s_expand_px){EnableWindow(s_focus_x_edit,FALSE);EnableWindow(s_focus_y_edit,FALSE);}
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
