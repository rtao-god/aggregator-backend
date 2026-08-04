internal static class WorkerTemplateWriter
{
    public static void Write(CatalogMediaGenerationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Directory.CreateDirectory(context.WorkerDirectory);
        Write("Catalog.Media.Worker.csproj", Project());
        Write("CatalogMediaWorkerOptions.cs", Options());
        Write("ClamAvCatalogMediaScanner.cs", Scanner());
        Write("ImageMagickCatalogMediaVariantProcessor.cs", VariantProcessor());
        Write("CatalogMediaOwnerWorker.cs", HostedWorker());
        Write("Program.cs", ProgramSource());
        Write("Dockerfile", Dockerfile());

        void Write(string name, string content) =>
            File.WriteAllText(Path.Combine(context.WorkerDirectory, name), content.Trim() + Environment.NewLine);
    }

    private static string Project() =>
        """
        <Project Sdk="Microsoft.NET.Sdk.Worker">
          <ItemGroup>
            <PackageReference Include="Microsoft.Extensions.Hosting" />
          </ItemGroup>
          <ItemGroup>
            <ProjectReference Include="../Catalog.Media.Application/Catalog.Media.Application.csproj" />
            <ProjectReference Include="../Catalog.Media.Domain/Catalog.Media.Domain.csproj" />
            <ProjectReference Include="../Catalog.Media.Infrastructure/Catalog.Media.Infrastructure.csproj" />
            <ProjectReference Include="../../BuildingBlocks/Platform.Messaging/Platform.Messaging.csproj" />
            <ProjectReference Include="../../BuildingBlocks/Platform.ObjectStorage/Platform.ObjectStorage.csproj" />
            <ProjectReference Include="../../BuildingBlocks/Platform.Observability/Platform.Observability.csproj" />
          </ItemGroup>
        </Project>
        """;

    private static string Options() =>
        """
        using Microsoft.Extensions.Configuration;
        using Platform.Messaging;

        namespace Aggregator.CatalogMedia.Worker;

        public sealed record CatalogMediaWorkerOptions
        {
            public const string SectionName = "CatalogMediaWorker";
            public required string CatalogConnectionString { get; init; }
            public required Uri BrokerUri { get; init; }
            public required string Exchange { get; init; }
            public required string WorkerIdentity { get; init; }
            public required Guid SystemActorId { get; init; }
            public required string ClamAvHost { get; init; }
            public int ClamAvPort { get; init; } = 3310;
            public int MaximumAttempts { get; init; } = 8;
            public int OutboxBatchSize { get; init; } = 50;
            public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);
            public TimeSpan EmptyDelay { get; init; } = TimeSpan.FromSeconds(2);

            public static CatalogMediaWorkerOptions FromConfiguration(IConfiguration configuration)
            {
                ArgumentNullException.ThrowIfNull(configuration);
                var brokerValue = Require(configuration, "Messaging:BrokerUri");
                if (!Uri.TryCreate(brokerValue, UriKind.Absolute, out var brokerUri))
                    throw new InvalidOperationException("Messaging:BrokerUri must be an absolute URI.");
                if (!Guid.TryParse(Require(configuration, $"{SectionName}:SystemActorId"), out var actorId) || actorId == Guid.Empty)
                    throw new InvalidOperationException($"{SectionName}:SystemActorId must be a non-empty UUID.");
                var options = new CatalogMediaWorkerOptions
                {
                    CatalogConnectionString = configuration.GetConnectionString("Catalog")
                        ?? throw new InvalidOperationException("Connection string 'Catalog' is required."),
                    BrokerUri = brokerUri,
                    Exchange = Require(configuration, "Messaging:Exchange"),
                    WorkerIdentity = Require(configuration, $"{SectionName}:WorkerIdentity"),
                    SystemActorId = actorId,
                    ClamAvHost = Require(configuration, $"{SectionName}:ClamAvHost"),
                    ClamAvPort = ReadInt(configuration, $"{SectionName}:ClamAvPort", 3310),
                    MaximumAttempts = ReadInt(configuration, $"{SectionName}:MaximumAttempts", 8),
                    OutboxBatchSize = ReadInt(configuration, $"{SectionName}:OutboxBatchSize", 50),
                    LeaseDuration = TimeSpan.FromSeconds(ReadInt(configuration, $"{SectionName}:LeaseDurationSeconds", 300)),
                    EmptyDelay = TimeSpan.FromMilliseconds(ReadInt(configuration, $"{SectionName}:EmptyDelayMilliseconds", 2000)),
                };
                options.Validate();
                return options;
            }

            public void Validate()
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(CatalogConnectionString);
                if (BrokerUri.Scheme is not ("amqp" or "amqps")) throw new ArgumentException("Broker URI must use AMQP.", nameof(BrokerUri));
                ValidateIdentity(Exchange, nameof(Exchange), 255);
                ValidateIdentity(WorkerIdentity, nameof(WorkerIdentity), 200);
                ValidateIdentity(ClamAvHost, nameof(ClamAvHost), 255);
                if (SystemActorId == Guid.Empty) throw new ArgumentException("System actor ID is required.", nameof(SystemActorId));
                if (ClamAvPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(ClamAvPort));
                if (MaximumAttempts is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(MaximumAttempts));
                if (LeaseDuration < TimeSpan.FromSeconds(10) || LeaseDuration > TimeSpan.FromMinutes(30))
                    throw new ArgumentOutOfRangeException(nameof(LeaseDuration));
                if (EmptyDelay < TimeSpan.FromMilliseconds(100) || EmptyDelay > TimeSpan.FromMinutes(5))
                    throw new ArgumentOutOfRangeException(nameof(EmptyDelay));
                CreateOutboxOptions().Validate();
                CreatePublisherOptions().Validate();
            }

            public OutboxDispatcherOptions CreateOutboxOptions() => new()
            {
                ConnectionString = CatalogConnectionString,
                Schema = "media_messaging",
                DispatcherIdentity = WorkerIdentity,
                BatchSize = OutboxBatchSize,
                MaximumDeliveryAttempts = MaximumAttempts,
                LeaseDuration = TimeSpan.FromMinutes(2),
                EmptyDelay = EmptyDelay,
            };

            public RabbitMqPublisherOptions CreatePublisherOptions() => new()
            {
                BrokerUri = BrokerUri,
                Exchange = Exchange,
                ClientProvidedName = WorkerIdentity,
            };

            private static string Require(IConfiguration configuration, string path) =>
                configuration[path] is { Length: > 0 } value ? value.Trim()
                    : throw new InvalidOperationException($"Configuration value '{path}' is required.");

            private static int ReadInt(IConfiguration configuration, string path, int fallback) =>
                configuration[path] is null ? fallback : int.TryParse(
                    configuration[path],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed)
                        ? parsed
                        : throw new InvalidOperationException($"Configuration value '{path}' must be an integer.");

            private static void ValidateIdentity(string value, string parameterName, int maximumLength)
            {
                if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
                    throw new ArgumentException("Runtime identity is invalid.", parameterName);
            }
        }
        """;

    private static string Scanner() =>
        """
        using System.Buffers.Binary;
        using System.Net.Sockets;
        using System.Text;
        using Aggregator.CatalogMedia.Application;

        namespace Aggregator.CatalogMedia.Worker;

        public sealed class ClamAvCatalogMediaScanner(CatalogMediaWorkerOptions options) : ICatalogMediaScanner
        {
            public async Task<CatalogMediaScanResult> ScanAsync(Stream content, CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(content);
                using var client = new TcpClient();
                await client.ConnectAsync(options.ClamAvHost, options.ClamAvPort, cancellationToken);
                await using var network = client.GetStream();
                await network.WriteAsync("zINSTREAM\0"u8.ToArray(), cancellationToken);
                var buffer = new byte[64 * 1024];
                var lengthBuffer = new byte[4];
                while (true)
                {
                    var read = await content.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, read);
                    await network.WriteAsync(lengthBuffer, cancellationToken);
                    await network.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                Array.Clear(lengthBuffer);
                await network.WriteAsync(lengthBuffer, cancellationToken);
                await network.FlushAsync(cancellationToken);
                using var response = new MemoryStream();
                while (true)
                {
                    var read = await network.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    response.Write(buffer, 0, read);
                    if (buffer.AsSpan(0, read).Contains((byte)0)) break;
                }
                var message = Encoding.UTF8.GetString(response.ToArray()).TrimEnd('\0', '\r', '\n');
                if (message.EndsWith("OK", StringComparison.Ordinal)) return new CatalogMediaScanResult(true, null);
                if (message.EndsWith("FOUND", StringComparison.Ordinal))
                {
                    var separator = message.LastIndexOf(':');
                    var threat = separator >= 0 ? message[(separator + 1)..].Replace("FOUND", "", StringComparison.Ordinal).Trim() : "unknown";
                    return new CatalogMediaScanResult(false, threat);
                }
                throw new CatalogMediaApplicationException(
                    "CatalogMedia.Scanner",
                    "CATALOG_MEDIA_SCANNER_RESPONSE_INVALID",
                    503,
                    $"ClamAV returned an unsupported response: '{message}'.",
                    "Restore the scanner service before retrying media processing.");
            }
        }
        """;

    private static string VariantProcessor() =>
        """
        using System.Diagnostics;
        using Aggregator.CatalogMedia.Application;
        using Aggregator.CatalogMedia.Domain;

        namespace Aggregator.CatalogMedia.Worker;

        public sealed class ImageMagickCatalogMediaVariantProcessor : ICatalogMediaVariantProcessor
        {
            public async Task<IReadOnlyList<CatalogMediaVariantContent>> CreateVariantsAsync(
                string sourceContentType,
                Stream source,
                CancellationToken cancellationToken)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(sourceContentType);
                ArgumentNullException.ThrowIfNull(source);
                var extension = sourceContentType switch
                {
                    "image/jpeg" => "jpg",
                    "image/png" => "png",
                    "image/webp" => "webp",
                    _ => throw Failure(
                        "CATALOG_MEDIA_SOURCE_TYPE_UNSUPPORTED",
                        $"Image source type '{sourceContentType}' is unsupported."),
                };
                var directory = Path.Combine(
                    Path.GetTempPath(),
                    "aggregator",
                    "catalog-media",
                    Guid.CreateVersion7().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    var input = Path.Combine(directory, $"source.{extension}");
                    await using (var output = File.Create(input))
                    {
                        await source.CopyToAsync(output, cancellationToken);
                    }
                    var originalDimensions = await IdentifyAsync(input, cancellationToken);
                    var result = new List<CatalogMediaVariantContent>
                    {
                        new(
                            CatalogMediaVariantKind.Original,
                            sourceContentType,
                            await File.ReadAllBytesAsync(input, cancellationToken),
                            originalDimensions.Width,
                            originalDimensions.Height),
                    };
                    result.Add(await CreateWebpAsync(input, directory, CatalogMediaVariantKind.Thumbnail, "320x320>", cancellationToken));
                    result.Add(await CreateWebpAsync(input, directory, CatalogMediaVariantKind.Card, "800x600>", cancellationToken));
                    result.Add(await CreateWebpAsync(input, directory, CatalogMediaVariantKind.Gallery, "1600x1200>", cancellationToken));
                    return result;
                }
                finally
                {
                    try
                    {
                        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }

            private static async Task<CatalogMediaVariantContent> CreateWebpAsync(
                string input,
                string directory,
                CatalogMediaVariantKind kind,
                string geometry,
                CancellationToken cancellationToken)
            {
                var output = Path.Combine(directory, $"{kind.ToString().ToLowerInvariant()}.webp");
                await RunAsync(
                    [input, "-auto-orient", "-strip", "-thumbnail", geometry, "-quality", "82", output],
                    cancellationToken);
                var dimensions = await IdentifyAsync(output, cancellationToken);
                return new CatalogMediaVariantContent(
                    kind,
                    "image/webp",
                    await File.ReadAllBytesAsync(output, cancellationToken),
                    dimensions.Width,
                    dimensions.Height);
            }

            private static async Task<(int Width, int Height)> IdentifyAsync(
                string path,
                CancellationToken cancellationToken)
            {
                var output = await RunAsync(["identify", "-format", "%w,%h", path], cancellationToken);
                var parts = output.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || !int.TryParse(parts[0], out var width) || !int.TryParse(parts[1], out var height))
                    throw Failure("CATALOG_MEDIA_IDENTIFY_INVALID", "ImageMagick returned invalid dimensions.");
                return (width, height);
            }

            private static async Task<string> RunAsync(
                IReadOnlyList<string> arguments,
                CancellationToken cancellationToken)
            {
                var start = new ProcessStartInfo("magick")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)
                    ?? throw Failure("CATALOG_MEDIA_IMAGEMAGICK_START_FAILED", "ImageMagick process could not start.");
                var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                var output = await standardOutput;
                var error = await standardError;
                if (process.ExitCode != 0)
                    throw Failure(
                        "CATALOG_MEDIA_IMAGEMAGICK_FAILED",
                        $"ImageMagick failed with exit code '{process.ExitCode}': {error.Trim()}"[..Math.Min(
                            $"ImageMagick failed with exit code '{process.ExitCode}': {error.Trim()}".Length,
                            2000)]);
                return output;
            }

            private static CatalogMediaApplicationException Failure(string code, string message) =>
                new("CatalogMedia.Variants", code, 422, message,
                    "Correct the source image or restore the ImageMagick runtime before retrying.");
        }
        """;

    private static string HostedWorker() =>
        """
        using Aggregator.CatalogMedia.Application;
        using Microsoft.Extensions.Hosting;
        using Platform.Messaging;

        namespace Aggregator.CatalogMedia.Worker;

        public sealed class CatalogMediaOwnerWorker(
            CatalogMediaProcessingService processing,
            PostgresOutboxDispatcher outbox,
            CatalogMediaWorkerOptions options) : BackgroundService
        {
            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var processed = await processing.ProcessOneAsync(
                        options.WorkerIdentity,
                        options.SystemActorId,
                        options.LeaseDuration,
                        options.MaximumAttempts,
                        stoppingToken);
                    var dispatched = await outbox.DispatchOnceAsync(stoppingToken);
                    if (!processed && dispatched == 0)
                        await Task.Delay(options.EmptyDelay, stoppingToken);
                }
            }
        }
        """;

    private static string ProgramSource() =>
        """
        using Aggregator.CatalogMedia.Application;
        using Aggregator.CatalogMedia.Infrastructure;
        using Aggregator.CatalogMedia.Worker;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;
        using Platform.Messaging;
        using Platform.ObjectStorage;
        using Platform.Observability;

        var builder = Host.CreateApplicationBuilder(args);
        var options = CatalogMediaWorkerOptions.FromConfiguration(builder.Configuration);
        builder.Services.AddSingleton(options);
        builder.Services.AddCatalogMediaApplication();
        AddObjectStore(builder);
        builder.Services.AddCatalogMediaInfrastructure(builder.Configuration);
        builder.Services.AddSingleton<ICatalogMediaScanner, ClamAvCatalogMediaScanner>();
        builder.Services.AddSingleton<ICatalogMediaVariantProcessor, ImageMagickCatalogMediaVariantProcessor>();
        builder.Services.AddSingleton(options.CreatePublisherOptions());
        builder.Services.AddSingleton(options.CreateOutboxOptions());
        builder.Services.AddSingleton<RabbitMqEventPublisher>();
        builder.Services.AddSingleton<IIntegrationEventPublisher>(services =>
            services.GetRequiredService<RabbitMqEventPublisher>());
        builder.Services.AddSingleton<PostgresOutboxDispatcher>();
        builder.Services.AddHostedService<CatalogMediaOwnerWorker>();
        builder.Services.AddPlatformObservability(builder.Configuration, "catalog-media-worker");
        await builder.Build().RunAsync();

        static void AddObjectStore(HostApplicationBuilder builder)
        {
            var objectOptions = new S3ObjectStoreOptions
            {
                ServiceUrl = new Uri(Require(builder.Configuration, "CatalogMedia:ObjectStorage:ServiceUrl"), UriKind.Absolute),
                Region = builder.Configuration["CatalogMedia:ObjectStorage:Region"] ?? "us-east-1",
                Bucket = Require(builder.Configuration, "CatalogMedia:ObjectStorage:Bucket"),
                AccessKey = Require(builder.Configuration, "CatalogMedia:ObjectStorage:AccessKey"),
                SecretKey = Require(builder.Configuration, "CatalogMedia:ObjectStorage:SecretKey"),
                ForcePathStyle = bool.TryParse(builder.Configuration["CatalogMedia:ObjectStorage:ForcePathStyle"], out var force)
                    ? force : true,
            };
            objectOptions.Validate();
            builder.Services.AddSingleton<IObjectStore>(_ => new S3ObjectStore(objectOptions));
        }

        static string Require(IConfiguration configuration, string path) =>
            configuration[path] is { Length: > 0 } value ? value.Trim()
                : throw new InvalidOperationException($"Configuration value '{path}' is required.");
        """;

    private static string Dockerfile() =>
        """
        FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
        ARG BUILD_CONFIGURATION=Release
        WORKDIR /src
        COPY . .
        RUN dotnet restore src/Catalog/Catalog.Media.Worker/Catalog.Media.Worker.csproj
        RUN dotnet publish src/Catalog/Catalog.Media.Worker/Catalog.Media.Worker.csproj \
            --configuration ${BUILD_CONFIGURATION} \
            --no-restore \
            --output /app/publish \
            /p:UseAppHost=false

        FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
        USER root
        RUN apt-get update \
            && apt-get install --yes --no-install-recommends imagemagick \
            && rm -rf /var/lib/apt/lists/*
        WORKDIR /app
        COPY --from=build /app/publish .
        USER $APP_UID
        ENTRYPOINT ["dotnet", "Catalog.Media.Worker.dll"]
        """;
}
