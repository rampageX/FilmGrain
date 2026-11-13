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

static HWND s_window, s_view, s_time_edit;
static WNDPROC s_original_edit_proc;
static pl_log s_log;
static pl_d3d11 s_device;
static pl_swapchain s_swapchain;
static pl_renderer s_renderer;
static pl_tex s_textures[3];
static pl_tex s_plate_textures[3];
static struct pl_frame s_image;
static enum pl_color_transfer s_hdr_transfer;
static bool s_source_full, s_hdr_display;
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
enum { PANEL_HEIGHT = 195, MSG_SEEK_FRAME = WM_APP+7 };
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
    if (RegCreateKeyExW(HKEY_CURRENT_USER,s_window_key,0,NULL,0,KEY_SET_VALUE,
                        NULL,&key,NULL) != ERROR_SUCCESS) return;
    RegSetValueExW(key,L"WindowBounds",0,REG_BINARY,(const BYTE *)&bounds,sizeof(bounds));
    RegCloseKey(key);
}

static void restore_window_bounds(HWND window)
{
    HKEY key;
    if (RegOpenKeyExW(HKEY_CURRENT_USER,s_window_key,0,KEY_QUERY_VALUE,&key) != ERROR_SUCCESS)
        return;
    FgsSavedWindowBounds bounds;
    DWORD type = 0, bytes = sizeof(bounds);
    LONG result = RegQueryValueExW(key,L"WindowBounds",NULL,&type,(BYTE *)&bounds,&bytes);
    RegCloseKey(key);
    if (result != ERROR_SUCCESS || type != REG_BINARY || bytes != sizeof(bounds)) return;

    RECT rect = bounds.normal;
    int width = rect.right-rect.left, height = rect.bottom-rect.top;
    if (width < 820 || width > 16384 || height < 420 || height > 16384) return;
    /* Monitor removal must not resurrect the window outside all displays. */
    HMONITOR monitor = MonitorFromRect(&rect,MONITOR_DEFAULTTONULL);
    MONITORINFO info = {0};
    info.cbSize = sizeof(info);
    if (!monitor || !GetMonitorInfoW(monitor,&info)) return;
    RECT work = info.rcWork;
    if (width > work.right-work.left) width = work.right-work.left;
    if (height > work.bottom-work.top) height = work.bottom-work.top;
    if (width < 820 || height < 420) return;
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
    if (s_fgsim_mode || s_digital_mode) {
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
      ights[i] * bytes_per_sample;
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
 * the GPU swapchain. No GDI-over-D3D overlays or repeated video uploads. */
static void track_bounds(RECT *client, int *left, int *right, int *top)
{
    *left = 144;
    *right = client->right - 190;
    if (*right < *left+24) *right = *left+24;
    *top = client->bottom-PANEL_HEIGHT;
}

/* One source of truth for painting, hit testing and resizing. The edit box
 * is deliberately only as large as the timestamp, with a shared bottom edge. */
static void bottom_frame_controls(const RECT *client, RECT *edit,
                                  RECT *random_btn, RECT *seek_btn, int *hint_left)
{
    int bottom = client->bottom-7;
    *edit = (RECT){14,bottom-s_time_edit_height,14+s_time_edit_width,bottom};
    *random_btn = (RECT){edit->right+12,bottom-31,edit->right+118,bottom};
    *seek_btn = (RECT){random_btn->right+12,bottom-31,random_btn->right+118,bottom};
    *hint_left = seek_btn->right+12;
}

static void draw_slider(HDC dc, RECT *client, int y, const wchar_t *name,
                        int percent, bool enabled, const wchar_t *label_value)
{
    int left, right, top;
    track_bounds(client, &left, &right, &top);
    (void)top;
    SetBkMode(dc, TRANSPARENT);
    SetTextColor(dc, enabled ? RGB(222,235,225) : RGB(125,133,129));
    RECT label = {12, y-15, left-13, y+15};
    DrawTextW(dc, name, -1, &label, DT_RIGHT|DT_VCENTER|DT_SINGLELINE);

    RECT rail = {left, y-3, right, y+3};
    HBRUSH background = CreateSolidBrush(RGB(63,73,67));
    FillRect(dc, &rail, background);
    DeleteObject(background);

    int handle_x = left+(right-left)*percent/100;
    RECT filled = {left, y-3, handle_x, y+3};
    HBRUSH green = CreateSolidBrush(enabled ? RGB(73,199,127) : RGB(87,101,92));
    FillRect(dc, &filled, green);
    HGDIOBJ old_brush = SelectObject(dc, green);
    HGDIOBJ old_pen = SelectObject(dc, GetStockObject(NULL_PEN));
    Ellipse(dc, handle_x-9, y-9, handle_x+9, y+9);
    SelectObject(dc, old_pen);
    SelectObject(dc, old_brush);
    DeleteObject(green);

    wchar_t value[32];
    swprintf(value, 32, L"%d%%", percent);
    SetTextColor(dc, RGB(229,240,232));
    RECT amount = {right+17, y-15, client->right-12, y+15};
    DrawTextW(dc, label_value ? label_value : value, -1, &amount, DT_LEFT|DT_VCENTER|DT_SINGLELINE);
}

static void paint_sliders(HWND window)
{
    PAINTSTRUCT ps;
    HDC dc = BeginPaint(window, &ps);
    RECT client;
    GetClientRect(window, &client);
    RECT panel = {0, client.bottom-PANEL_HEIGHT, client.right, client.bottom};
    FillRect(dc, &panel, s_panel_brush);
    HGDIOBJ old_font = s_panel_font ? SelectObject(dc, s_panel_font) : NULL;
    int y = panel.top+32;
    wchar_t digital_value[16];
    swprintf(digital_value,16,L"%.2f",s_strength/100.0);
    int grain_position = s_fgsim_mode ? s_strength*100/3 :
        s_digital_mode ? (s_strength-15)*100/85 : s_strength;
    const wchar_t *value = s_fgsim_mode ? s_fgsim_names[s_strength] :
        s_digital_mode ? digital_value : NULL;
    draw_slider(dc,&client,y,s_fgsim_mode?L"FGSIM 档位":L"颗粒强度",
                s_display_grain?grain_position:0,s_has_grain,
                (s_fgsim_mode || s_digital_mode) && !s_display_grain?L"关闭 · G":value);
    draw_slider(dc, &client, y+45, L"LUT 强度",
                s_lut_source && s_lut_enabled ? s_lut_strength : 0,
                s_lut_source != NULL,NULL);
    if (s_video_path) {
        int left, right, unused;
        track_bounds(&client, &left, &right, &unused);
        int frame_y = y+90;
        RECT label = {12, frame_y-15, left-13, frame_y+15};
        SetTextColor(dc, RGB(222,235,225));
        SetBkMode(dc, TRANSPARENT);
        DrawTextW(dc, L"选择视频帧", -1, &label, DT_RIGHT|DT_VCENTER|DT_SINGLELINE);
        RECT rail = {left, frame_y-3, right, frame_y+3};
        HBRUSH background = CreateSolidBrush(RGB(63,73,67));
        FillRect(dc, &rail, background);DeleteObject(background);
        double proportion = s_duration_seconds > 0 ? s_pending_seconds/s_duration_seconds : 0.0;
        if (proportion < 0) proportion = 0;
        if (proportion > 1) proportion = 1;
        int handle_x = left+(int)((right-left)*proportion+0.5);
        RECT filled = {left, frame_y-3, handle_x, frame_y+3};
        HBRUSH green = CreateSolidBrush(RGB(73,199,127));
        FillRect(dc, &filled, green);
        HGDIOBJ old_brush=SelectObject(dc,green),old_pen=SelectObject(dc,GetStockObject(NULL_PEN));
        Ellipse(dc,handle_x-8,frame_y-8,handle_x+8,frame_y+8);
        SelectObject(dc,old_pen);SelectObject(dc,old_brush);DeleteObject(green);
        wchar_t duration[80];
        swprintf(duration,80,L"%.1f / %.1f s",s_pending_seconds,s_duration_seconds);
        RECT amount={right+12,frame_y-15,client.right-10,frame_y+15};
        SetTextColor(dc,RGB(229,240,232));
        DrawTextW(dc,duration,-1,&amount,DT_LEFT|DT_VCENTER|DT_SINGLELINE);
    }
    RECT edit, random_btn, seek_btn;
    int hint_left = 14;
    if (s_video_path) {
        bottom_frame_controls(&client,&edit,&random_btn,&seek_btn,&hint_left);
        HBRUSH gray=CreateSolidBrush(RGB(57,67,61));
        FillRect(dc,&random_btn,gray);FillRect(dc,&seek_btn,gray);
        SetTextColor(dc,RGB(244,249,246));
        DrawTextW(dc,L"随机一帧",-1,&random_btn,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
        DrawTextW(dc,L"跳转",-1,&seek_btn,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
        DeleteObject(gray);
    }
    if (s_frame_loading) {
        RECT loading={hint_left,client.bottom-38,client.right-256,client.bottom-7};
        SetTextColor(dc,RGB(255,191,105));
        DrawTextW(dc,L"正在抽取所选视频帧…",-1,&loading,
                  DT_LEFT|DT_VCENTER|DT_SINGLELINE);
    }
    RECT hint={hint_left,client.bottom-38,client.right-256,client.bottom-7};
    if (!s_frame_loading) {
        SetTextColor(dc,RGB(142,165,152));
        DrawTextW(dc,s_pixel_view?L"双击画面：适应窗口  |  拖动：平移 1:1":L"双击画面：1:1 原始像素",
                  -1,&hint,DT_LEFT|DT_VCENTER|DT_SINGLELINE|DT_END_ELLIPSIS);
    }
    if (s_result_path) {
        RECT apply = {client.right-244, client.bottom-38, client.right-134, client.bottom-7};
        RECT cancel = {client.right-123, client.bottom-38, client.right-13, client.bottom-7};
        HBRUSH green = CreateSolidBrush(RGB(38,129,78));
        HBRUSH gray = CreateSolidBrush(RGB(57,67,61));
        FillRect(dc, &apply, green); FillRect(dc, &cancel, gray);
        SetTextColor(dc, RGB(244,249,246));
        SetBkMode(dc, TRANSPARENT);
        DrawTextW(dc, L"应用到 FGS", -1, &apply, DT_CENTER|DT_VCENTER|DT_SINGLELINE);
        DrawTextW(dc, L"取消", -1, &cancel, DT_CENTER|DT_VCENTER|DT_SINGLELINE);
        DeleteObject(green); DeleteObject(gray);
    }
    if (old_font) SelectObject(dc, old_font);
    EndPaint(window, &ps);
}

static void redraw_controls(void)
{
    if (s_window) {
        RECT client; GetClientRect(s_window, &client);
        RECT panel = {0, client.bottom-PANEL_HEIGHT, client.right, client.bottom};
        InvalidateRect(s_window, &panel, FALSE);
    }
    if (s_view) InvalidateRect(s_view, NULL, FALSE);
}

static void format_frame_