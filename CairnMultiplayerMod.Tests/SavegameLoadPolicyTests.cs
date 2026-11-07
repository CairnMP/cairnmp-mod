using System;
using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class SavegameLoadPolicyTests
{
    [Fact]
    public void AMissingStateCoroutineIsTheNullRoutineFromStartCoroutine()
    {
        Assert.True(SavegameLoadPolicy.IsMissingStateCoroutine(
            new NullReferenceException(SavegameLoadPolicy.MissingCoroutineMessage)));
        Assert.True(SavegameLoadPolicy.IsMissingStateCoroutine(
            new InvalidOperationException(
                "wrapped",
                new NullReferenceException("StartCoroutine failed: routine is null"))));
    }

    [Fact]
    public void AnyOtherSaveExceptionStillFailsTheLoad()
    {
        Assert.False(SavegameLoadPolicy.IsMissingStateCoroutine(null));
        Assert.False(SavegameLoadPolicy.IsMissingStateCoroutine(
            new NullReferenceException("Object reference not set to an instance of an object.")));
        Assert.False(SavegameLoadPolicy.IsMissingStateCoroutine(
            new InvalidOperationException(SavegameLoadPolicy.MissingCoroutineMessage)));
    }
}
