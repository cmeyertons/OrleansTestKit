﻿using System.Linq.Expressions;
using Moq;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestKit.Reminders;
using Orleans.TestKit.Services;
using Orleans.TestKit.Storage;
using Orleans.TestKit.Streams;
using Orleans.TestKit.Timers;
using Orleans.Timers;

namespace Orleans.TestKit;

/// <summary>
/// The central abstraction for the OrleansTestKit -- stands up multiple services and provides a centralized abstraction for interacting with test grains
/// </summary>
public sealed class TestKitSilo
{
    private readonly List<IGrainBase> _activatedGrains = new();

    private readonly TestGrainCreator _grainCreator;

    private readonly TestGrainLifecycle _grainLifecycle = new();

    /// <summary>
    ///     Flag indicating if a grain has already been created in this test silo. Since this is all mocked up only the
    ///     grain under test should be real, therefore only a single grain should ever be created.
    /// </summary>
    private bool _isGrainCreated;

    /// <summary>
    /// Initializes a new instance of the <see cref="TestKitSilo"/> class.
    /// </summary>
    public TestKitSilo()
    {
        GrainFactory = new TestGrainFactory(Options);
        ServiceProvider = new TestServiceProvider(Options);
        StorageManager = new StorageManager(Options);
        TimerRegistry = new TestTimerRegistry();
        ReminderRegistry = new TestReminderRegistry();
        StreamProviderManager = new TestStreamProviderManager(Options);
        ServiceProvider.AddService<IKeyedServiceCollection<string, IStreamProvider>>(StreamProviderManager);
        ServiceProvider.AddService<IReminderRegistry>(ReminderRegistry);
        GrainRuntime = new TestGrainRuntime(GrainFactory, TimerRegistry, ReminderRegistry, ServiceProvider, StorageManager);
        ServiceProvider.AddService<IGrainRuntime>(GrainRuntime);
        _grainCreator = new TestGrainCreator(GrainRuntime, ServiceProvider);
    }

    /// <summary>
    /// Gets the silo's grain runtime instance that is injected into the created grain
    /// </summary>
    public TestGrainRuntime GrainRuntime { get; }

    /// <summary>
    ///     Gets the silo grain factory used by the test grain with creating other grains This should only be used by
    ///     the grain, not any test code.
    /// </summary>
    public TestGrainFactory GrainFactory { get; }

    /// <summary>Gets the test silo options.</summary>
    public TestKitOptions Options { get; } = new();

    /// <summary>Gets the manager of all test silo reminders.</summary>
    public TestReminderRegistry ReminderRegistry { get; }

    /// <summary>Gets the service provider used when creating new instances.</summary>
    public TestServiceProvider ServiceProvider { get; }

    /// <summary>Gets the manager of all test silo storage.</summary>
    public StorageManager StorageManager { get; }

    /// <summary>Gets the manager of all test silo streams.</summary>
    public TestStreamProviderManager StreamProviderManager { get; }

    /// <summary>Gets the manager of all test silo timers.</summary>
    public TestTimerRegistry TimerRegistry { get; }

    /// <summary>
    /// Create a grain - will be injected with a grain context and participate in grain lifecycle
    /// </summary>
    /// <typeparam name="T">The grain type (class not interface)</typeparam>
    /// <param name="id">The grain's long id</param>
    /// <returns>The grain</returns>
    public Task<T> CreateGrainAsync<T>(long id)
        where T : Grain, IGrainWithIntegerKey =>
        CreateGrainAsync<T>(GrainIdKeyExtensions.CreateIntegerKey(id));

    /// <summary>
    /// Create a grain - will be injected with a grain context and participate in grain lifecycle
    /// </summary>
    /// <typeparam name="T">The grain type (class not interface)</typeparam>
    /// <param name="id">The grain's id</param>
    /// <returns>The grain</returns>
    public Task<T> CreateGrainAsync<T>(Guid id)
        where T : Grain, IGrainWithGuidKey =>
        CreateGrainAsync<T>(GrainIdKeyExtensions.CreateGuidKey(id));

    /// <summary>
    /// Create a grain - will be injected with a grain context and participate in grain lifecycle
    /// </summary>
    /// <typeparam name="T">The grain type (class not interface)</typeparam>
    /// <param name="id">The grain's id</param>
    /// <returns>The grain</returns>
    public Task<T> CreateGrainAsync<T>(string id)
        where T : Grain, IGrainWithStringKey
        => CreateGrainAsync<T>(IdSpan.Create(id));

    /// <summary>
    /// Create a grain - will be injected with a grain context and participate in grain lifecycle
    /// </summary>
    /// <typeparam name="T">The grain type (class not interface)</typeparam>
    /// <param name="id">The grain's id</param>
    /// <param name="keyExtension">The key extension</param>
    /// <returns>The grain</returns>
    public Task<T> CreateGrainAsync<T>(Guid id, string keyExtension)
        where T : Grain, IGrainWithGuidCompoundKey
        => CreateGrainAsync<T>(GrainIdKeyExtensions.CreateGuidKey(id, keyExtension));

    /// <summary>
    /// Create a grain - will be injected with a grain context and participate in grain lifecycle
    /// </summary>
    /// <typeparam name="T">The grain type (class not interface)</typeparam>
    /// <param name="id">The grain's id</param>
    /// <param name="keyExtension">The key extension</param>
    /// <returns>The grain</returns>
    public Task<T> CreateGrainAsync<T>(long id, string keyExtension)
        where T : Grain, IGrainWithIntegerCompoundKey
        => CreateGrainAsync<T>(GrainIdKeyExtensions.CreateIntegerKey(id, keyExtension));

    /// <summary>Deactivate the given <see cref="Grain"/>.</summary>
    /// <param name="grain">Grain to Deactivate.</param>
    /// <param name="deactivationReason">
    ///     Reason which will passed to the Grian <seealso cref="DeactivationReason"/>.
    /// </param>
    /// <param name="cancellationToken">Token which will passed to the grain.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task DeactivateAsync(Grain grain, DeactivationReason? deactivationReason = null, CancellationToken cancellationToken = default)
    {
        if (grain == null)
        {
            throw new ArgumentNullException(nameof(grain));
        }

        await _grainLifecycle.TriggerStopAsync().ConfigureAwait(false);

        deactivationReason ??= new DeactivationReason(DeactivationReasonCode.ShuttingDown, $"TestKit {nameof(TestKitSilo.DeactivateAsync)} called");
        await grain.OnDeactivateAsync(deactivationReason.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fetches Grain Context.</summary>
    /// <param name="grain">Grain to fetch Context from.</param>
    /// <returns><see cref="IGrainContext"/>.</returns>
    /// <exception cref="NotSupportedException">Grain does not derive from <see cref="IGrainBase"/>.</exception>
    public IGrainContext GetContextFromGrain(Grain grain)
    {
        if (grain is IGrainBase grainbase)
        {
            return grainbase.GrainContext;
        }
        else
        {
            throw new NotSupportedException($"Current Grain does not derive from {nameof(IGrainBase)} can therefore not fetch " +
                $"{nameof(IGrainContext)}");
        }
    }

    /// <summary>Fetches <see cref="GrainId"/> from current Grain.</summary>
    /// <param name="grain">Grain to fetch Grain Id.</param>
    /// <returns><see cref="GrainId"/>.</returns>
    public GrainId GetGrainId(Grain grain) => GrainId.Parse(grain.IdentityString);

    /// <summary>
    /// Used for interacting with test reminders -- this will be removed in a later version as Orleans has alleviated the need for this.
    /// </summary>
    /// <param name="grain">The grain to create the context for</param>
    /// <param name="token">Cancellation token</param>
    /// <returns>A disposable to denote when the context is finished.</returns>
    public async Task<IDisposable> GetReminderActivationContext(Grain grain, CancellationToken token = default)
    {
        var handler = new ReminderContextHandler();

        try
        {
            await handler.SetActivationContext(GetContextFromGrain(grain), token).ConfigureAwait(false);
        }
        catch
        {
            // Release Thread to avoid Deadlocks when an Exception happens.
            handler.Dispose();
            throw;
        }

        return handler;
    }

    /// <summary>
    /// Verify the grain runtime object's mock
    /// </summary>
    /// <param name="expression">The expression to use</param>
    /// <param name="times">Number of times expected</param>
    [Obsolete("Use the Runtime property instead. This will be removed in a future version.")]
    public void VerifyRuntime(Expression<Action<IGrainRuntime>> expression, Func<Times> times) =>
        GrainRuntime.Mock.Verify(expression, times);

    private async Task<T> CreateGrainAsync<T>(IdSpan identity, CancellationToken cancellation = default)
        where T : Grain
    {
        if (_isGrainCreated)
        {
            throw new Exception(
                "A grain has already been created in this silo. Only 1 grain per test silo should ever be created. Add grain probes for supporting grains.");
        }

        _isGrainCreated = true;
        var grainContext = new TestGrainActivationContext
        {
            GrainId = GrainId.Create(new GrainType(identity), identity),
            ActivationServices = ServiceProvider,

            // GrainIdentity = identity,
            GrainType = typeof(T),
            ObservableLifecycle = _grainLifecycle,
        };

        // make context injectable so grain dependency components can inject IGrainContext directly themselves
        ServiceProvider.AddService<IGrainContext>(grainContext);

        // Create a stateless grain
        var grain = _grainCreator.CreateGrainInstance<T>(grainContext);
        switch (grain)
        {
            case null:
                throw new Exception($"Unable to instantiate grain {typeof(T)} properly");

            // Check if there are any reminders for this grain and set the reminder target
            case IRemindable remindable:
                ReminderRegistry.SetGrainTarget(remindable);
                break;
        }

        // Trigger the lifecycle hook that will get the grain's state from the runtime
        await _grainLifecycle.TriggerStartAsync().ConfigureAwait(false);

        // Due to update the OnActivate call has to be done manually not all Grains implement ILifecycle anymore
        if (grain is IGrainBase)
        {
            // Used to enable reminder context on during activate
            using var reminderContext = grain is IRemindable
                ? await GetReminderActivationContext(grain, cancellation).ConfigureAwait(false)
                : null
            ;

            await grain.OnActivateAsync(cancellation).ConfigureAwait(false);
            _activatedGrains.Add(grain);
        }

        return (T)grain;
    }
}
