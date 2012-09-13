
//=============================================================================
//
// Debug assertion tools
//
//=============================================================================
#ifndef __AGS_CN_DEBUG__ASSERT_H
#define __AGS_CN_DEBUG__ASSERT_H

#ifdef _DEBUG

// TODO: revise this later (add platform-specific output maybe?)
#if defined(WINDOWS_VERSION)

inline void assert(bool expr)
{
    if (!expr) {
        _asm {
            int 3
        }
    }
}

inline void assert(bool expr, const char *err_msg)
{
    if (!expr) {
        _asm {
            int 3
        }
    }
}

#else // !WINDOWS_VERSION

#include <assert.h>

#endif

#else // !_DEBUG

// JJS: GCC has trouble with macro overloading
inline void assert(bool expr) {}
inline void assert(bool expr, const char *err_msg) {}

#endif // !_DEBUG

#endif // __AGS_CN_DEBUG__ASSERT_H
