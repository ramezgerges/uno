#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using Uno.UI.Hosting;
using Uno.UI.Runtime.Skia;
using Uno.UI.Runtime.Skia.Win32;
using Uno.WinUI.Runtime.Skia.X11;

namespace SamplesApp;

internal static class Program
{
	private static App? _app;

	[STAThread]
	public static void Main(string[] args)
	{
		// args are not forwarded to the host: SamplesApp reads the command line through
		// Environment.GetCommandLineArgs() (see App.xaml.cs), which the runtime-test harness relies on.

		// Ensures that we're loading the Skia assemblies properly as the output is
		// adjusted to avoid getting reference assemblies in the output folder.
		AssemblyLoadContext.Default.Resolving += Default_Resolving;

		Run();
	}

	private static void Run()
	{
		App.ConfigureLogging(); // Enable tracing of the host

		UnoPlatformHost? host = default;
		var builder = UnoPlatformHostBuilder.Create()
			.App(() => _app = new App())
			.AfterInit(() =>
			{
				if (host is X11ApplicationHost)
				{
					global::Uno.Foundation.Extensibility.ApiExtensibility.Register<Microsoft.Web.WebView2.Core.CoreWebView2>(typeof(Microsoft.Web.WebView2.Core.INativeWebViewProvider), o => new global::Uno.UI.WebView.Skia.X11.X11NativeWebViewProvider(o));
				}
			})
			// Headless is always "supported", so it must only be offered when explicitly asked for,
			// otherwise Build() would pick it over the real desktop hosts.
			.UseVncIfRequested()
			.UseX11(hostBuilder => hostBuilder.PreloadMediaPlayer(true))
			.UseWin32(hostBuilder => hostBuilder.PreloadMediaPlayer(true))
			.UseLinuxFrameBuffer(hostBuilder => hostBuilder.XkbKeymap(new(layout: "us,ara", options: "grp:alt_shift_toggle")))
			.UseMacOS();

		host = builder
			.Build();

		host.Run();
	}

	/// <summary>
	/// Adds the headless host wired to a VNC server when <c>--vnc[=port]</c> is on the command line,
	/// streaming the app to any standard viewer. Without the switch this is a no-op and the normal
	/// desktop hosts are used.
	/// </summary>
	private static IUnoPlatformHostBuilder UseVncIfRequested(this IUnoPlatformHostBuilder builder)
	{
		var arg = Environment.GetCommandLineArgs().FirstOrDefault(a => a is "--vnc" || a.StartsWith("--vnc=", StringComparison.Ordinal));

		if (arg is null)
		{
			return builder;
		}

		var port = arg.Contains('=') && int.TryParse(arg.AsSpan(arg.IndexOf('=') + 1), out var parsed) ? parsed : 5900;

		var input = new HeadlessInput();
		var frames = new HeadlessFrameSource();
		var fpsArg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--vnc-fps=", StringComparison.Ordinal));
		var maxFps = fpsArg is not null && int.TryParse(fpsArg.AsSpan("--vnc-fps=".Length), out var parsedFps) ? parsedFps : 60;

		var server = new VncServer(port, frames, input, maxFps);
		server.Start();

		// Browsers cannot speak RFB, so the same window is also served as JPEG on the next port.
		new WebFrameServer(port + 1, frames, input).Start();

		return builder.UseHeadless(headless => headless
			.WithSize(VncWidth, VncHeight)
			.WithInput(input)
			.WithFrames(frames));
	}

	private const int VncWidth = 1280;
	private const int VncHeight = 800;

	private static System.Reflection.Assembly? Default_Resolving(AssemblyLoadContext alc, System.Reflection.AssemblyName assemblyName)
	{
		try
		{
			if (Uri.TryCreate(typeof(Program).Assembly.Location, UriKind.Absolute, out var asm))
			{
				var appPath = Path.GetDirectoryName(asm.LocalPath)!;

				var asmPath = Path.Combine(appPath, assemblyName.Name! + ".dll");

				if (File.Exists(asmPath))
				{
					return alc.LoadFromAssemblyPath(asmPath);
				}
			}

			return null;
		}
		catch (Exception e)
		{
			Console.WriteLine(e);
			Console.WriteLine($"Error processing {assemblyName.Name}. SamplesApp head assembly location: {typeof(Program).Assembly.Location}");
			throw;
		}
	}
}
