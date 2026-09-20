namespace Cli.Services;

public sealed class LifetimeService
{
	private static readonly Dictionary<Type, object> _services = [];
	
	public static void Add<TInterface, TImplementation>()
		where TInterface : class
		where TImplementation : class, TInterface, new()
	{
		if (!typeof(TInterface).IsInterface)
			throw new ArgumentException($"{nameof(TInterface)} must be an interface.");
			
		var instance = new TImplementation();
		
		_services.Add(typeof(TInterface), instance);
	}
	
	public static void Add(object implementation)
	{
		_services.Add(implementation.GetType(), implementation);
	}
	
	public static TInterface Get<TInterface>() 
		where TInterface : class
	{
		if (!_services.TryGetValue(typeof(TInterface), out var service))
            throw new InvalidOperationException(
                $"No service registered for {typeof(TInterface).Name}.");

        return (TInterface)service;
	}
}
