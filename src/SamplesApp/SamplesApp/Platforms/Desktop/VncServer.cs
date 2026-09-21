#nullable enable

using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Windows.System;
using Uno.UI.Runtime.Skia;
using System.Threading;

namespace SamplesApp;

/// <summary>
/// A minimal RFB 3.8 server: no security, Raw encoding, framebuffer updates only. Streams the headless
/// SamplesApp window to any standard VNC viewer. Input messages are parsed and discarded, so the stream
/// is view-only. Raw encoding sends a full frame per update, which is only sensible over loopback.
/// </summary>
internal sealed class VncServer
{
	private readonly TcpListener _listener;
	private readonly HeadlessFrameSource _frames;

	private readonly HeadlessInput? _input;
	private readonly int _maxFramesPerSecond;
	private int _lastButtonMask;

	public VncServer(int port, HeadlessFrameSource frames, HeadlessInput? input = null, int maxFramesPerSecond = 60)
	{
		_maxFramesPerSecond = maxFramesPerSecond;
		_listener = new TcpListener(IPAddress.Loopback, port);
		_frames = frames;
		_input = input;
	}

	public void Start()
	{
		_listener.Start();
		Console.WriteLine($"VNC listening on {_listener.LocalEndpoint}");

		new Thread(AcceptLoop) { IsBackground = true, Name = "VNC accept" }.Start();
	}

	private void AcceptLoop()
	{
		while (true)
		{
			var client = _listener.AcceptTcpClient();
			Console.WriteLine($"VNC client connected: {client.Client.RemoteEndPoint}");
			new Thread(() => ServeClient(client)) { IsBackground = true, Name = "VNC client" }.Start();
		}
	}

	/// <summary>
	/// Per-connection handshake between the reader and the sender. The reader must never block on frame
	/// production: while it is waiting for something to draw it cannot read pointer or key messages, and
	/// since input is usually what causes the next repaint, that deadlocks until the wait times out.
	/// </summary>
	private sealed class ClientState
	{
		public readonly object Gate = new();
		public bool UpdatePending;
		public bool Incremental;
		public bool Closed;
	}

	private void ServeClient(TcpClient client)
	{
		try
		{
			using (client)
			{
				client.NoDelay = true;
				using var stream = client.GetStream();

				Handshake(stream);

				var state = new ClientState();
				var sender = new Thread(() => SendLoop(stream, state)) { IsBackground = true, Name = "VNC sender" };
				sender.Start();

				try
				{
					ReadLoop(stream, state);
				}
				finally
				{
					lock (state.Gate)
					{
						state.Closed = true;
						Monitor.PulseAll(state.Gate);
					}

					sender.Join(TimeSpan.FromSeconds(1));
				}
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"VNC client ended: {ex.Message}");
		}
	}

	/// <summary>Drains client messages. Input is applied immediately; frame requests are handed off.</summary>
	private void ReadLoop(NetworkStream stream, ClientState state)
	{
		var header = new byte[1];

		while (true)
		{
			ReadExactly(stream, header);

			switch (header[0])
			{
				case 0: // SetPixelFormat — we only ever serve our own format
					ReadExactly(stream, new byte[19]);
					break;

				case 2: // SetEncodings
				{
					var head = new byte[3];
					ReadExactly(stream, head);
					var count = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(1));
					ReadExactly(stream, new byte[count * 4]);
					break;
				}

				case 3: // FramebufferUpdateRequest
				{
					var body = new byte[9];
					ReadExactly(stream, body);

					lock (state.Gate)
					{
						// Requests coalesce: a client that asks twice before we draw gets one frame.
						state.UpdatePending = true;
						state.Incremental = body[0] != 0;
						Monitor.PulseAll(state.Gate);
					}

					break;
				}

				case 4: // KeyEvent
				{
					var body = new byte[7];
					ReadExactly(stream, body);
					OnKeyEvent(body[0] != 0, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(3)));
					break;
				}

				case 5: // PointerEvent
				{
					var body = new byte[5];
					ReadExactly(stream, body);
					OnPointerEvent(
						body[0],
						BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(1)),
						BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(3)));
					break;
				}

				case 6: // ClientCutText
				{
					var cut = new byte[7];
					ReadExactly(stream, cut);
					var length = BinaryPrimitives.ReadUInt32BigEndian(cut.AsSpan(3));
					ReadExactly(stream, new byte[length]);
					break;
				}

				default:
					throw new InvalidOperationException($"Unsupported RFB client message type {header[0]}.");
			}
		}
	}

	private void SendLoop(NetworkStream stream, ClientState state)
	{
		var sentFrameId = -1L;
		var pacer = new FramePacer(_maxFramesPerSecond);
		var sendBuffer = Array.Empty<byte>();
		var damage = new List<HeadlessRect>();
		var wire = new MemoryStream();
		var idle = false;

		try
		{
			while (true)
			{
				bool incremental;

				lock (state.Gate)
				{
					while (!state.UpdatePending && !state.Closed)
					{
						Monitor.Wait(state.Gate);
					}

					if (state.Closed)
					{
						return;
					}

					state.UpdatePending = false;
					incremental = state.Incremental;
				}

				// Only back off once a frame came back empty. Gating every render on a fresh invalidation
				// throttles the pipeline, because presenting is what drives it forward: each frame would
				// otherwise wait out the input-to-invalidation round trip before drawing.
				if (idle)
				{
					_frames.WaitForRenderRequest(ref sentFrameId, TimeSpan.FromMilliseconds(100));
				}

				pacer.WaitForNextFrame();
				idle = !SendFrame(stream, wire, incremental, ref sentFrameId, ref sendBuffer, damage);
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"VNC sender ended: {ex.Message}");
		}
	}

	private void Handshake(NetworkStream stream)
	{
		// ProtocolVersion
		stream.Write(Encoding.ASCII.GetBytes("RFB 003.008\n"));
		ReadExactly(stream, new byte[12]);

		// Security: advertise None, expect it back, then report success.
		stream.Write(new byte[] { 1, 1 });
		var chosen = new byte[1];
		ReadExactly(stream, chosen);
		stream.Write(new byte[4]);

		// ClientInit (shared flag, ignored)
		ReadExactly(stream, new byte[1]);

		// Render once up front so ServerInit can advertise the real size.
		var probe = Array.Empty<byte>();
		var probeGeneration = -1L;
		HeadlessFrameInfo first;

		while (!_frames.TryRenderInto(ref probe, ref probeGeneration, waitForNew: false, damage: null, out first))
		{
			Thread.Sleep(16);
		}

		var width = first.Width;
		var height = first.Height;

		var name = Encoding.ASCII.GetBytes("Uno Headless");
		var init = new byte[24 + name.Length];
		BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(0), (ushort)width);
		BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(2), (ushort)height);
		WritePixelFormat(init.AsSpan(4));
		BinaryPrimitives.WriteUInt32BigEndian(init.AsSpan(20), (uint)name.Length);
		name.CopyTo(init.AsSpan(24));
		stream.Write(init);
	}

	/// <summary>
	/// 32bpp true colour. The buffer is BGRA8888 little-endian, so as a little-endian u32 the channels
	/// sit at red&lt;&lt;16 | green&lt;&lt;8 | blue, which is what the shifts below declare.
	/// </summary>
	private static void WritePixelFormat(Span<byte> target)
	{
		target[0] = 32;   // bits per pixel
		target[1] = 24;   // depth
		target[2] = 0;    // big endian flag
		target[3] = 1;    // true colour flag
		BinaryPrimitives.WriteUInt16BigEndian(target[4..], 255);   // red max
		BinaryPrimitives.WriteUInt16BigEndian(target[6..], 255);   // green max
		BinaryPrimitives.WriteUInt16BigEndian(target[8..], 255);   // blue max
		target[10] = 16;  // red shift
		target[11] = 8;   // green shift
		target[12] = 0;   // blue shift
		// 3 bytes padding
	}

	/// <summary>Renders and writes one update. Returns false when nothing had changed.</summary>
	private bool SendFrame(
		NetworkStream stream,
		MemoryStream wire,
		bool incremental,
		ref long sentFrameId,
		ref byte[] sendBuffer,
		List<HeadlessRect> damage)
	{
		if (!_frames.TryRenderInto(ref sendBuffer, ref sentFrameId, waitForNew: false, damage, out var info))
		{
			return false;
		}

		// A non-incremental request means the client wants everything, whatever actually changed.
		if (!incremental)
		{
			damage.Clear();
			damage.Add(new HeadlessRect(0, 0, info.Width, info.Height));
		}

		// The update is assembled in memory and written once. Writing per rectangle row turned a frame
		// into hundreds of small sends, which cost far more in latency than the pixels ever did.
		wire.SetLength(0);

		Span<byte> head = stackalloc byte[4];
		head.Clear();
		BinaryPrimitives.WriteUInt16BigEndian(head[2..], (ushort)damage.Count);
		wire.Write(head);

		foreach (var rect in damage)
		{
			Span<byte> header = stackalloc byte[12];
			BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)rect.X);
			BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)rect.Y);
			BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)rect.Width);
			BinaryPrimitives.WriteUInt16BigEndian(header[6..], (ushort)rect.Height);
			BinaryPrimitives.WriteInt32BigEndian(header[8..], 0); // Raw encoding
			wire.Write(header);

			var byteOffset = rect.X * 4;
			var byteCount = rect.Width * 4;

			for (var row = rect.Y; row < rect.Y + rect.Height; row++)
			{
				wire.Write(sendBuffer, row * info.Stride + byteOffset, byteCount);
			}
		}

		wire.Position = 0;
		wire.CopyTo(stream);

		return damage.Count > 0;
	}

	/// <summary>
	/// Translates an RFB PointerEvent into injected pointer input. RFB reports an absolute position plus
	/// a button-state bitmask, so transitions are derived by diffing against the previous mask.
	/// </summary>
	private void OnPointerEvent(byte buttonMask, ushort x, ushort y)
	{
		if (_input is not { } input)
		{
			return;
		}

		input.MovePointer(x, y);

		var changed = buttonMask ^ _lastButtonMask;
		_lastButtonMask = buttonMask;

		// Bits 0-2 are left/middle/right; bits 3-6 carry wheel notches as press-and-release pairs.
		TryButton(0, HeadlessPointerButton.Left);
		TryButton(1, HeadlessPointerButton.Middle);
		TryButton(2, HeadlessPointerButton.Right);

		// A wheel "press" is one notch. WinUI uses 120 units per notch.
		if ((buttonMask & (1 << 3)) != 0) { input.ScrollPointer(120, x, y); }
		if ((buttonMask & (1 << 4)) != 0) { input.ScrollPointer(-120, x, y); }
		if ((buttonMask & (1 << 5)) != 0) { input.ScrollPointer(-120, x, y, isHorizontal: true); }
		if ((buttonMask & (1 << 6)) != 0) { input.ScrollPointer(120, x, y, isHorizontal: true); }

		void TryButton(int bit, HeadlessPointerButton button)
		{
			if ((changed & (1 << bit)) == 0)
			{
				return;
			}

			if ((buttonMask & (1 << bit)) != 0)
			{
				input.PressPointer(button, x, y);
			}
			else
			{
				input.ReleasePointer(button, x, y);
			}
		}
	}

	/// <summary>Translates an RFB KeyEvent (an X11 keysym) into injected keyboard input.</summary>
	private void OnKeyEvent(bool isDown, uint keysym)
	{
		if (_input is not { } input)
		{
			return;
		}

		var key = KeysymToVirtualKey(keysym);

		if (isDown)
		{
			// Printable Latin-1 keysyms are their own character, which is what text input needs.
			var character = keysym is >= 0x20 and <= 0x7E ? (char)keysym : (char?)null;
			input.PressKey(key, character);
		}
		else
		{
			input.ReleaseKey(key);
		}
	}

	private static VirtualKey KeysymToVirtualKey(uint keysym) => keysym switch
	{
		0xFF08 => VirtualKey.Back,
		0xFF09 => VirtualKey.Tab,
		0xFF0D => VirtualKey.Enter,
		0xFF1B => VirtualKey.Escape,
		0xFF50 => VirtualKey.Home,
		0xFF51 => VirtualKey.Left,
		0xFF52 => VirtualKey.Up,
		0xFF53 => VirtualKey.Right,
		0xFF54 => VirtualKey.Down,
		0xFF55 => VirtualKey.PageUp,
		0xFF56 => VirtualKey.PageDown,
		0xFF57 => VirtualKey.End,
		0xFF63 => VirtualKey.Insert,
		0xFFFF => VirtualKey.Delete,
		0xFFE1 => VirtualKey.LeftShift,
		0xFFE2 => VirtualKey.RightShift,
		0xFFE3 => VirtualKey.LeftControl,
		0xFFE4 => VirtualKey.RightControl,
		0xFFE9 => VirtualKey.LeftMenu,
		0xFFEA => VirtualKey.RightMenu,
		0xFFEB => VirtualKey.LeftWindows,
		0xFFEC => VirtualKey.RightWindows,
		0x0020 => VirtualKey.Space,
		>= 0x30 and <= 0x39 => (VirtualKey)keysym,              // 0-9
		>= 0x41 and <= 0x5A => (VirtualKey)keysym,              // A-Z
		>= 0x61 and <= 0x7A => (VirtualKey)(keysym - 0x20),     // a-z map onto the same virtual keys
		>= 0xFFBE and <= 0xFFC9 => VirtualKey.F1 + (int)(keysym - 0xFFBE),
		_ => VirtualKey.None,
	};

	private static void ReadExactly(NetworkStream stream, byte[] buffer)
	{
		if (buffer.Length == 0)
		{
			return;
		}

		stream.ReadExactly(buffer, 0, buffer.Length);
	}
}
