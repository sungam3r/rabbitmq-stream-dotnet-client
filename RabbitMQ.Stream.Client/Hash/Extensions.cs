﻿// This source code is dual-licensed under the Apache License, version
// 2.0, and the Mozilla Public License, version 2.0.
// Copyright (c) 2007-2020 VMware, Inc.

using System;

namespace RabbitMQ.Stream.Client.Hash;

internal static class Extensions
{
#if NETFX45
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
    internal static uint ToUInt32(this byte[] data, int start)
    {
        return BitConverter.IsLittleEndian
                ? (uint)(data[start] | data[start + 1] << 8 | data[start + 2] << 16 | data[start + 3] << 24)
                : (uint)(data[start] << 24 | data[start + 1] << 16 | data[start + 2] << 8 | data[start + 3]);
    }

#if NETFX45
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
    internal static ulong ToUInt64(this byte[] data, int start)
    {
        if (BitConverter.IsLittleEndian)
        {
            var i1 = (uint)(data[start] | data[start + 1] << 8 | data[start + 2] << 16 | data[start + 3] << 24);
            var i2 = (ulong)(data[start + 4] | data[start + 5] << 8 | data[start + 6] << 16 | data[start + 7] << 24);
            return (i1 | i2 << 32);
        }
        else
        {
            var i1 = (ulong)(data[start] << 24 | data[start + 1] << 16 | data[start + 2] << 8 | data[start + 3]);
            var i2 = (uint)(data[start + 4] << 24 | data[start + 5] << 16 | data[start + 6] << 8 | data[start + 7]);
            return (i2 | i1 << 32);

            //int i1 = (*pbyte << 24) | (*(pbyte + 1) << 16) | (*(pbyte + 2) << 8) | (*(pbyte + 3));
            //int i2 = (*(pbyte + 4) << 24) | (*(pbyte + 5) << 16) | (*(pbyte + 6) << 8) | (*(pbyte + 7));
            //return (uint)i2 | ((long)i1 << 32);
        }
    }

#if NETFX45
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
    internal static uint RotateLeft(this uint x, byte r)
    {
        return (x << r) | (x >> (32 - r));
    }

#if NETFX45
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
    internal static ulong RotateLeft(this ulong x, byte r)
    {
        return (x << r) | (x >> (64 - r));
    }

#if NETFX45
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
    internal static uint FMix(this uint h)
    {
        // pipelining friendly algorithm
        h = (h ^ (h >> 16)) * 0x85ebca6b;
        h = (h ^ (h >> 13)) * 0xc2b2ae35;
        return h ^ (h >> 16);
    }

#if NETFX45
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
    internal static ulong FMix(this ulong h)
    {
        // pipelining friendly algorithm
        h = (h ^ (h >> 33)) * 0xff51afd7ed558ccd;
        h = (h ^ (h >> 33)) * 0xc4ceb9fe1a85ec53;

        return (h ^ (h >> 33));
    }
}
