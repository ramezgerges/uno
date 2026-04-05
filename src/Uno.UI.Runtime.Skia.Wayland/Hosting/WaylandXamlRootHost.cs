using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Uno.UI.Hosting;
using Windows.UI.Core;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal partial class WaylandXamlRootHost : IXamlRootHost
{
	private static readonly ConcurrentDictionary<Window, WaylandXamlRootHost> _windowToHost = new();
	private static bool _firstWindowCreated;

	private readonly Window _window;
	private readonly WaylandWindowWrapper _wrapper;
	private readonly TaskCompletionSource _closedTcs = new();
	private readonly AutoResetEvent _renderEvent = new(false);

	private WaylandPointerInputSource? _pointerSource;
	private WaylandKeyboardInputSource? _keyboardSource;

	internal WaylandXamlRootHost(WaylandWindowWrapper wrapper, Window window, XamlRoot xamlRoot)
	{
		_wrapper = wrapper;
		_window = window;
		_windowToHost[window] = this;
		_firstWindowCreated = true;
	}

	public Task Closed => _closedTcs.Task;

	/// <summary>
	/// The EGL display associated with this host, set during renderer initialization.
	/// </summary>
	internal IntPtr EglDisplay { get; set; }

	UIElement? IXamlRootHost.RootElement => _window.RootElement;

	void IXamlRootHost.InvalidateRender()
	{
		_renderEvent.Set();
	}

	internal static WaylandXamlRootHost? GetHostFromWindow(Window window)
		=> _windowToHost.TryGetValue(window, out var host) ? host : null;

	internal static void CloseAllWindows()
	{
		foreach (var (window, host) in _windowToHost)
		{
			host.Close();
		}
	}

	internal static bool AllWindowsDone()
		=> _firstWindowCreated && _windowToHost.IsEmpty;

	internal void Close()
	{
		if (_windowToHost.TryRemove(_window, out _))
		{
			_closedTcs.TrySetResult();
		}
	}

	internal void SetPointerSource(WaylandPointerInputSource source) => _pointerSource = source;

	internal void SetKeyboardSource(WaylandKeyboardInputSource source) => _keyboardSource = source;

	internal WaylandPointerInputSource? PointerSource => _pointerSource;

	internal WaylandKeyboardInputSource? KeyboardSource => _keyboardSource;

	public static void QueueAction(IXamlRootHost host, Action action)
		=> host.RootElement?.Dispatcher.RunAsync(CoreDispatcherPriority.High, new DispatchedHandler(action));
}
