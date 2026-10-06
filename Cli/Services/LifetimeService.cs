namespace Cli.Services;

public sealed class LifetimeService
{
	private static readonly Dictionary<Type, Func<object>> _services = [];

	public static void Add<TInterface, TImplementation>()
		where TInterface : class
		where TImplementation : class, TInterface, new()
	{
		if (!typeof(TInterface).IsInterface)
			throw new ArgumentException($"{nameof(TInterface)} must be an interface.");

		_services.Add(typeof(TInterface), () => new TImplementation());
	}

	public static void Add(object implementation)
	{
		_services.Add(implementation.GetType(), () => implementation);
	}

	public static TInterface Get<TInterface>()
		where TInterface : class
	{
		if (!_services.TryGetValue(typeof(TInterface), out var serviceFactory))
            throw new InvalidOperationException(
                $"No service registered for {typeof(TInterface).Name}.");

        return (TInterface)serviceFactory();
	}
}