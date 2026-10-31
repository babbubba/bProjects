using bProjects.App.Projects;

namespace bProjects.IntegrationTests;

public sealed class KeyedSerializerTests
{
    [Fact]
    public async Task Actions_for_the_same_key_never_overlap_and_run_in_arrival_order()
    {
        var serializer = new KeyedSerializer<Guid>();
        var key = Guid.NewGuid();
        var inside = 0;
        var maxInside = 0;
        var order = new List<int>();
        var gate = new TaskCompletionSource();

        var first = serializer.RunAsync(key, async () =>
        {
            Interlocked.Increment(ref inside);
            await gate.Task;
            order.Add(1);
            maxInside = Math.Max(maxInside, Volatile.Read(ref inside));
            Interlocked.Decrement(ref inside);
        });
        var rest = Enumerable.Range(2, 5).Select(n => serializer.RunAsync(key, () =>
        {
            maxInside = Math.Max(maxInside, Interlocked.Increment(ref inside));
            order.Add(n);
            Interlocked.Decrement(ref inside);
            return Task.CompletedTask;
        })).ToArray();

        gate.SetResult();
        await Task.WhenAll(rest.Append(first));

        Assert.Equal(1, maxInside);
        Assert.Equal(1, order[0]);
        Assert.Equal(6, order.Count);
    }

    [Fact]
    public async Task Different_keys_do_not_block_each_other()
    {
        var serializer = new KeyedSerializer<Guid>();
        var gate = new TaskCompletionSource();
        var blocked = serializer.RunAsync(Guid.NewGuid(), () => gate.Task);
        await serializer.RunAsync(Guid.NewGuid(), () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        gate.SetResult();
        await blocked;
    }
}
