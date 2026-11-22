using System;
using System.Threading;
using System.Threading.Tasks;
using Tatami.Models;

namespace Tatami.Services;

public sealed class BuildCoordinator
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public async Task<BuildResult> BuildAsync(SiteConfig config, IProgress<string>? log = null, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return config.Builder switch
            {
                "builtin" => await new SiteBuilder().BuildAsync(config, cancellationToken),
                "external" => await new ExternalSiteBuilder().BuildAsync(config, log, cancellationToken),
                _ => throw new ArgumentException($"不明なビルダーです: {config.Builder}")
            };
        }
        finally { Gate.Release(); }
    }
}
