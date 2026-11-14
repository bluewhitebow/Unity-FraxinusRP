#ifndef FRAXINUS_AREA_LIGHT_KEYWORDS_INCLUDED
#define FRAXINUS_AREA_LIGHT_KEYWORDS_INCLUDED

// A pipeline build capability, fixed ON/OFF by native and managed filtering.
// Keep the material's original shader target; low-target passes still omit LTC.
#pragma multi_compile_fragment _ _FRAXINUS_AREA_LIGHTS

#endif
