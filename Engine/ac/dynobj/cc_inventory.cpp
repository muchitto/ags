//=============================================================================
//
// Adventure Game Studio (AGS)
//
// Copyright (C) 1999-2011 Chris Jones and 2011-20xx others
// The full list of copyright holders can be found in the Copyright.txt
// file, which is part of this source code distribution.
//
// The AGS source code is provided under the Artistic License 2.0.
// A copy of this license can be found in the file License.txt and at
// http://www.opensource.org/licenses/artistic-license-2.0.php
//
//=============================================================================
#include "ac/dynobj/cc_inventory.h"
#include "ac/dynobj/scriptinvitem.h"
#include "ac/characterinfo.h"
#include "util/stream.h"

using namespace AGS::Common;

extern ScriptInvItem scrInv[MAX_INV];

// return the type name of the object
const char *CCInventory::GetType() {
    return "Inventory";
}

size_t CCInventory::CalcSerializeSize()
{
    return sizeof(int32_t);
}

// serialize the object into BUFFER (which is BUFSIZE bytes)
// return number of bytes used
void CCInventory::Serialize(const char *address, Stream *out) {
    ScriptInvItem *shh = (ScriptInvItem*)address;
    out->WriteInt32(shh->id);
}

void CCInventory::Unserialize(int index, Stream *in, size_t data_sz) {
    int num = in->ReadInt32();
    ccRegisterUnserializedObject(index, &scrInv[num], this);
}