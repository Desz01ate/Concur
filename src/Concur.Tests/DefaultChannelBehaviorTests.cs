namespace Concur.Tests;

using Abstractions;
using Implementations;

/// <summary>
/// Runs the shared <see cref="BoundedChannelBehaviorTests"/> contract against
/// <see cref="DefaultChannel{T}"/> (bounded mode).
/// </summary>
public class DefaultChannelBehaviorTests : BoundedChannelBehaviorTests
{
    protected override IChannel<int> CreateChannel(int capacity) =>
        new DefaultChannel<int>(capacity);
}
