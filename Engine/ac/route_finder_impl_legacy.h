//=============================================================================
//
// Adventure Game Studio (AGS)
//
// Copyright (C) 1999-2011 Chris Jones and 2011-2024 various contributors
// The full list of copyright holders can be found in the Copyright.txt
// file, which is part of this source code distribution.
//
// The AGS source code is provided under the Artistic License 2.0.
// A copy of this license can be found in the file License.txt and at
// https://opensource.org/license/artistic-2-0/
//
//=============================================================================

#ifndef __AC_ROUTE_FINDER_IMPL_LEGACY
#define __AC_ROUTE_FINDER_IMPL_LEGACY

// Forward declaration
namespace AGS { namespace Common { class Bitmap; }}
struct MoveList;

namespace AGS {
namespace Engine {
namespace RouteFinderLegacy {

void init_pathfinder();
void shutdown_pathfinder();

void set_wallscreen(AGS::Common::Bitmap *wallscreen);

int can_see_from(int x1, int y1, int x2, int y2);
void get_lastcpos(int &lastcx, int &lastcy);

void set_route_move_speed(int speed_x, int speed_y);

int find_route(short srcx, short srcy, short xx, short yy, AGS::Common::Bitmap *onscreen, int movlst, int nocross = 0, int ignore_walls = 0);
void calculate_move_stage(MoveList * mlsp, int aaa);

} // namespace RouteFinderLegacy
} // namespace Engine
} // namespace AGS

#endif // __AC_ROUTE_FINDER_IMPL_LEGACY