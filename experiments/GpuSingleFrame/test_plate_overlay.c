/* R10 shader math guard. This is not a GPU test; Windows/libplacebo
 * compilation and visual correspondence remain separate acceptance gates. */
#include <assert.h>
#include <math.h>
#include <stdio.h>

static double overlay(int top, int bottom, double opacity)
{
    /* Source P010 GPU unorm is code * 64/65535 (10-bit left shift). */
    double a = (double)top * 64.0 / 65535.0;
    double b = (double)bottom * 64.0 / 65535.0;
    double blended = a < 0.5 ? 2*a*b : 1-2*(1-a)*(1-b);
    return (a+(blended-a)*opacity)*65535.0/64.0;
}

int main(void)
{
    assert(fabs(overlay(400,800,0.0)-400.0) < 0.00001);
    /* FFmpeg CPU blend=overlay reference: top Y400, bottom Y800 -> Y624.
     * Floating-point Vulkan path and integer CPU rounding may differ by 2. */
    assert(fabs(overlay(400,800,1.0)-624.0) < 2.0);
    assert(overlay(400,800,1.0) > overlay(400,800,0.7));
    assert(overlay(400,800,0.7) > overlay(400,800,0.4));
    assert(overlay(400,800,0.4) > overlay(400,800,0.15));
    assert(fabs(overlay(400,512,1.0)-400.0) < 1.0);
    assert(overlay(700,800,1.0) > 700);
    puts("PASS: Grain Plate Overlay math (FFmpeg reference)");
    return 0;
}
