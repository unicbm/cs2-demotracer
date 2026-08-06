/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Text;
using BotHiderImpl;

namespace DemoTracer.Tests;

public sealed class BotHiderSharedMemoryEncodingTests
{
    [Theory]
    [InlineData("abc", 4)]
    [InlineData("😀", 5)]
    [InlineData("选手", 7)]
    public void FixedUtf8FieldAcceptsCompleteNullTerminatedValues(
        string value,
        int fieldLength)
    {
        Assert.True(SharedMemoryClient.TryEncodeFixedUtf8(
            value,
            fieldLength,
            out var buffer));
        Assert.Equal(fieldLength, buffer.Length);
        Assert.Equal(value, Encoding.UTF8.GetString(buffer).TrimEnd('\0'));
        Assert.Equal(0, buffer[^1]);
    }

    [Theory]
    [InlineData("abcd", 4)]
    [InlineData("😀", 4)]
    [InlineData("选手", 6)]
    [InlineData("a", 0)]
    public void FixedUtf8FieldRejectsValuesWithoutNullTerminatorSpace(
        string value,
        int fieldLength)
    {
        Assert.False(SharedMemoryClient.TryEncodeFixedUtf8(
            value,
            fieldLength,
            out _));
    }
}
