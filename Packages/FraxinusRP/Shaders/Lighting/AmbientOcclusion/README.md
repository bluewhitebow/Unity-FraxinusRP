Ground-truth ambient occlusion
=============================

The GTAO horizon search and cosine-weighted arc integration are adapted from
AkiKurisu's IllusionRP (https://github.com/AkiKurisu/IllusionRP, revision 91495f7),
whose implementation references bladesero/GTAO_URP and Unity HDRP.

Fraxinus supplies its own per-camera RenderGraph scheduling, half/quarter depth
reductions, projection reconstruction, normal decoding and texture bindings.
The bilateral spatial filter and combined blur/upsample retain depth-aware
visibility reconstruction, using explicit float2 visibility/linear-depth data
and bounded texture loads instead of packed bit patterns or a shared depth atlas.
Half-resolution reconstruction predicts each sample's closest depth on the local
surface plane. Two consecutive depth intervals must agree before they define a
slope, preserving occlusion on tilted surfaces while rejecting depth discontinuities.
There is no temporal accumulation or bent-normal output.

The original repository license notice follows:

Licensed under the Unity Companion License for Unity-dependent projects--see
[Unity Companion License](http://www.unity3d.com/legal/licenses/Unity_Companion_License).

Unless expressly provided otherwise, the Software under this license is made
available strictly on an "AS IS" BASIS WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED. Please review the license for details on these and other terms and conditions.

The upstream blur/upsample implementation also credits Microsoft MiniEngine /
Minigraph and James Stanard. The original notice is retained here for attribution;
the Fraxinus compute shader uses a direct bilateral kernel instead of that
implementation's shared-memory SmartBlur kernel:

Copyright (c) Microsoft. All rights reserved.
This code is licensed under the MIT License (MIT).
THIS CODE IS PROVIDED *AS IS* WITHOUT WARRANTY OF
ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING ANY
IMPLIED WARRANTIES OF FITNESS FOR A PARTICULAR
PURPOSE, MERCHANTABILITY, OR NON-INFRINGEMENT.

Developed by Minigraph

Author: James Stanard
