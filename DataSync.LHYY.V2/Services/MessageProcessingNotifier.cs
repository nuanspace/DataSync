using System.Threading.Channels;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 通知后台消息处理引擎有新消息可领取。
/// </summary>
public sealed class MessageProcessingNotifier
{
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite
    });

    public void Notify()
    {
        _signals.Writer.TryWrite(true);
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            if (!await _signals.Reader.WaitToReadAsync(timeoutCts.Token))
                return;

            while (_signals.Reader.TryRead(out _))
            {
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }
}
