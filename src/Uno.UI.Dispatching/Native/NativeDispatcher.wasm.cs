using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.JavaScript;

using Uno.Foundation.Logging;

namespace Uno.UI.Dispatching
{
	internal sealed partial class NativeDispatcher
	{
		private const int NumberOfTimestampDeltaToKeep = 20;
		private static readonly double _dispatcherFrameTime = 1000.0 / 60;
		private static readonly List<double> _timestampDeltas = new(NumberOfTimestampDeltaToKeep);
		private static int _timestampsHead;
		private static double? _lastTimestamp;
		private static double _runningAverage;
		private static readonly int[] _numberOfConsecutiveJobsToRunPerDispatchItemsCallOptions = [0, 2, 4, 6];
		private static int _numberOfConsecutiveJobsToRunPerDispatchItemsCall;

		private long _lastDispatchRendering;

#pragma warning disable IDE0051 // Remove unused private members
		[JSExport]
		private static void DispatcherCallback(double timestamp)
#pragma warning restore IDE0051 // Remove unused private members
		{
			if (typeof(NativeDispatcher).Log().IsEnabled(LogLevel.Trace))
			{
				typeof(NativeDispatcher).Log().Trace($"[tid:{Environment.CurrentManagedThreadId}]: NativeDispatcher.DispatcherCallback()");
			}

			if (_lastTimestamp is { } lastTimestamp)
			{
				var delta = timestamp - lastTimestamp;
				if (_timestampDeltas.Count == NumberOfTimestampDeltaToKeep)
				{
					var removedDelta = _timestampDeltas[_timestampsHead];
					_timestampDeltas[_timestampsHead] = delta;
					_timestampsHead = (_timestampsHead + 1) % NumberOfTimestampDeltaToKeep;
					_runningAverage += (delta - removedDelta) / NumberOfTimestampDeltaToKeep;
				}
				else
				{
					_timestampDeltas.Add(delta);
					if (_timestampDeltas.Count == NumberOfTimestampDeltaToKeep)
					{
						_runningAverage = _timestampDeltas.Average();
					}
				}
			}
			_lastTimestamp = timestamp;
			if (_runningAverage != 0)
			{
				if (_runningAverage - _dispatcherFrameTime < 5)
				{
					if (_numberOfConsecutiveJobsToRunPerDispatchItemsCall != _numberOfConsecutiveJobsToRunPerDispatchItemsCallOptions.Length)
					{
						_numberOfConsecutiveJobsToRunPerDispatchItemsCall++;
					}
				}
				else
				{
					if (_numberOfConsecutiveJobsToRunPerDispatchItemsCall != 0)
					{
						_numberOfConsecutiveJobsToRunPerDispatchItemsCall--;
					}
				}
			}
			Console.WriteLine($"_numberOfConsecutiveJobsToRunPerDispatchItemsCall = {_numberOfConsecutiveJobsToRunPerDispatchItemsCall}");
			DispatchItems(_numberOfConsecutiveJobsToRunPerDispatchItemsCallOptions[_numberOfConsecutiveJobsToRunPerDispatchItemsCall]);
		}

		partial void Initialize()
		{
			if (typeof(NativeDispatcher).Log().IsEnabled(LogLevel.Trace))
			{
				typeof(NativeDispatcher).Log().Trace($"[tid:{Environment.CurrentManagedThreadId}]: NativeDispatcher.Initialize() IsThreadingSupported:{IsThreadingSupported}");
			}

			if (IsThreadingSupported && Environment.CurrentManagedThreadId != 1)
			{
				throw new InvalidOperationException($"NativeDispatcher must be initialized on the main thread.");
			}
		}

		internal static bool IsThreadingSupported { get; }
			= Environment.GetEnvironmentVariable("UNO_BOOTSTRAP_MONO_RUNTIME_FEATURES")
				?.Split(',').Contains("threads", StringComparer.OrdinalIgnoreCase) ?? false;

		private bool GetHasThreadAccess()
			=> !IsThreadingSupported || Environment.CurrentManagedThreadId == 1;

		/// <summary>
		/// Provide an action that will delegate the dispatch of CoreDispatcher work
		/// </summary>
		[EditorBrowsable(EditorBrowsableState.Never)]
		internal static Action<Action, NativeDispatcherPriority> DispatchOverride;

		partial void EnqueueNative(NativeDispatcherPriority priority)
		{
			if (typeof(NativeDispatcher).Log().IsEnabled(LogLevel.Trace))
			{
				typeof(NativeDispatcher).Log().Trace($"[tid:{Environment.CurrentManagedThreadId}]: NativeDispatcher.EnqueueNative()");
			}

			if (DispatchOverride == null)
			{
				if (IsThreadingSupported && Environment.CurrentManagedThreadId != 1)
				{
					// This is a separate function to avoid enclosing early resolution
					// by the interpreter/JIT, in case we're running the non-threaded
					// runtime.
					static void InvokeOnMainThread()
						=> WebAssembly.JSInterop.InternalCalls.InvokeOnMainThread();

					InvokeOnMainThread();
				}
			}
			else
			{
				DispatchOverride(NativeDispatcher.DispatchItems, priority);
			}
		}

		/// <summary>
		/// Synchronous dispatching to the dispatcher is required for Wasm.
		/// This must be called *only* when originating from the `requestAnimationFrame` callback
		/// </summary>
		partial void SynchronousDispatchRenderingPartial()
		{
			var elapsed = Stopwatch.GetElapsedTime(_lastDispatchRendering);
			var delta = TimeSpan.FromSeconds(1 / DispatchingFeatureConfiguration.DispatcherQueue.WebAssemblyFrameRate);
			if (elapsed < delta)
			{
				if (this.Log().IsTraceEnabled())
				{
					this.Log().Trace($"Skipping frame ({elapsed} < {delta})");
				}
				return;
			}

			_lastDispatchRendering = Stopwatch.GetTimestamp();

			if (IsRendering)
			{
				DispatchItems();
				RaiseRendered();
			}
		}
	}
}
