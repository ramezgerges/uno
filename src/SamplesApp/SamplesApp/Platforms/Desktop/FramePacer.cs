#nullable enable

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace SamplesApp;

/// <summary>
/// Holds a client to a maximum frame rate, standing in for the vsync a real display would impose.
/// </summary>
/// <remarks>
/// Without this a client that asks for frames in a loop runs unbounded: rendering is what advances Uno's
/// pipeline, so every request finds new content and the next one starts immediately. At 1280x800 with RFB
/// Raw encoding that is 4 MB per frame, so an uncapped viewer saturates the link and burns a core for
/// frames no one can perceive.
/// </remarks>
internal sealed class FramePacer
{
	[DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
	private static extern uint TimeBeginPeriod(uint period);

	static FramePacer()
	{
		// Windows rounds Thread.Sleep up to the system timer tick, 15.6ms by default, so asking for a
		// 5ms sleep inside an 8ms frame budget actually costs 15.6ms and pins the rate near 60fps
		// whatever the cap says. Requesting 1ms resolution makes short sleeps behave.
		if (OperatingSystem.IsWindows())
		{
			TimeBeginPeriod(1);
		}
	}

	private readonly long _minimumTicks;
	private long _nextTicks;

	public FramePacer(int maxFramesPerSecond)
		=> _minimumTicks = maxFramesPerSecond > 0
			? Stopwatch.Frequency / maxFramesPerSecond
			: 0;

	/// <summary>Blocks until the next frame is due, if it is not already.</summary>
	public void WaitForNextFrame()
	{
		if (_minimumTicks == 0)
		{
			return;
		}

		var now = Stopwatch.GetTimestamp();

		if (_nextTicks > now)
		{
			// Thread.Sleep resolves to roughly 15ms on Windows, so sleeping the whole remainder of a
			// 16ms budget routinely overshoots and settles well under the requested rate. Sleep the
			// bulk of it and spin the last couple of milliseconds, which costs little and lands close.
			var spinTicks = Stopwatch.Frequency / 500;
			var sleepTicks = _nextTicks - now - spinTicks;

			if (sleepTicks > 0)
			{
				Thread.Sleep((int)(sleepTicks * 1000 / Stopwatch.Frequency));
			}

			var spinner = new SpinWait();

			while (Stopwatch.GetTimestamp() < _nextTicks)
			{
				spinner.SpinOnce(sleep1Threshold: -1);
			}

			now = Stopwatch.GetTimestamp();
		}

		// Start from now rather than accumulating, so a slow frame does not leave a backlog to catch up on.
		_nextTicks = now + _minimumTicks;
	}
}
