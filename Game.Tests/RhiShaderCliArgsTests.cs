// SPDX-License-Identifier: MIT
// Tests for RhiShader.JoinCliArgs - the whitespace join helper that powers
// the multi-arg FromSource(... IReadOnlyList<string>?, IReadOnlyList<string>?)
// overload's cliArgs parameter.

using System;
using System.Collections.Generic;
using Engine.RHI;
using Xunit;

namespace Engine.Game.Tests;

public sealed class RhiShaderCliArgsTests
{
    [Fact]
    public void JoinCliArgs_Null_ReturnsNull()
    {
        Assert.Null(RhiShader.JoinCliArgs(null));
    }

    [Fact]
    public void JoinCliArgs_Empty_ReturnsNull()
    {
        Assert.Null(RhiShader.JoinCliArgs(new List<string>()));
    }

    [Fact]
    public void JoinCliArgs_SingleElement_ReturnsThatElementVerbatim()
    {
        Assert.Equal("-DFOO=1", RhiShader.JoinCliArgs(new[] { "-DFOO=1" }));
    }

    [Fact]
    public void JoinCliArgs_MultipleElements_ReturnsSpaceSeparated()
    {
        Assert.Equal(
            "-DFOO=1 -DBAR=2 -target metal",
            RhiShader.JoinCliArgs(new[] { "-DFOO=1", "-DBAR=2", "-target", "metal" }));
    }

    [Fact]
    public void JoinCliArgs_RoundTripOnSingleElement_IsIdentity()
    {
        // Caller passing a pre-joined string via the single-element list
        // gets the same string back. Helper does not canonicalise.
        string prejoined = "a b c";
        Assert.Equal(prejoined, RhiShader.JoinCliArgs(new[] { prejoined }));
    }

    [Fact]
    public void JoinCliArgs_EmptyEntriesArePreservedAsConsecutiveDelimiters()
    {
        // Documented contract: caller is responsible for filtering empties.
        // Helper preserves them so misuse produces observable double-spaces
        // downstream rather than silent collapse.
        Assert.Equal("a  b", RhiShader.JoinCliArgs(new[] { "a", "", "b" }));
    }

    [Fact]
    public void JoinCliArgs_PreprocessorDefinePair_RoundTripsToTwoArgvSlots()
    {
        // Sanity check on the actual use case: a "-D NAME=1" pair packs
        // into two argv slots via the joint whitespace, matching what the
        // Metal backend will splice back out via whitespace-tokenisation.
        string joined = RhiShader.JoinCliArgs(new[] { "-D", "PLUGIN_X=1" });
        Assert.Equal("-D PLUGIN_X=1", joined);
        string[] tokenised = joined!.Split(' ', 2);
        Assert.Equal(2, tokenised.Length);
        Assert.Equal("-D", tokenised[0]);
        Assert.Equal("PLUGIN_X=1", tokenised[1]);
    }
}
