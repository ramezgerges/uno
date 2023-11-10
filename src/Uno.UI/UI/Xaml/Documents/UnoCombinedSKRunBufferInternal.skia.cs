using System;
using System.Collections;
using System.Collections.Generic;
using SkiaSharp;

namespace SkiaSharp;

internal struct UnoCombinedSKRunBufferInternal : IEquatable<UnoCombinedSKRunBufferInternal>
{
	private UnoSKRunBufferInternal _buffer1;
	private int _size1;
	private UnoSKRunBufferInternal _buffer2;
	private int _size2;
	private UnoSKRunBufferInternal _buffer3;
	private int _size3;

	public UnoCombinedSKRunBufferInternal(UnoSKRunBufferInternal buffer1, int size1, UnoSKRunBufferInternal buffer2, int size2, UnoSKRunBufferInternal buffer3, int size3)
	{
		_buffer1 = buffer1;
		_buffer2 = buffer2;
		_buffer3 = buffer3;
		_size1 = size1;
		_size2 = size2;
		_size3 = size3;
	}

	public readonly bool Equals(UnoCombinedSKRunBufferInternal that)
	{
		return _buffer1.Equals(that._buffer1) && _buffer2.Equals(that._buffer2) && _buffer3.Equals(that._buffer3);
	}

	public override readonly bool Equals(object obj)
	{
		if (obj is UnoCombinedSKRunBufferInternal obj2)
		{
			return Equals(obj2);
		}

		return false;
	}

	public static bool operator ==(UnoCombinedSKRunBufferInternal left, UnoCombinedSKRunBufferInternal right)
	{
		return left.Equals(right);
	}

	public static bool operator !=(UnoCombinedSKRunBufferInternal left, UnoCombinedSKRunBufferInternal right)
	{
		return !left.Equals(right);
	}

	public override readonly int GetHashCode()
	{
		HashCode hashCode = default(HashCode);
		hashCode.Add(_buffer1.GetHashCode());
		hashCode.Add(_buffer2.GetHashCode());
		hashCode.Add(_buffer3.GetHashCode());
		return hashCode.ToHashCode();
	}

	public CombinedSpanView<SKPoint> GetPositionSpan() => new CombinedSpanView<SKPoint>(this);

	public CombinedSpanView<ushort> GetGlyphSpan() => new CombinedSpanView<ushort>(this);

	public struct CombinedSpanView<T> : IEnumerable<T>
	{
		private UnoCombinedSKRunBufferInternal _outer;
		private int _size;

		public CombinedSpanView(UnoCombinedSKRunBufferInternal outer)
		{
			_outer = outer;
			_size = _outer._size1 + _outer._size2 + _outer._size3;
		}

		public T this[int index]
		{
			get
			{
				unsafe
				{
					if (index < 0)
					{
						throw new IndexOutOfRangeException("Index out of range");
					}


					if (index < _outer._size1)
					{
						var span1 = new Span<T>(_outer._buffer1.pos, _outer._size1);
						return span1[index];
					}

					index -= _outer._size1;


					if (index < _outer._size2)
					{
						var span2 = new Span<T>(_outer._buffer2.pos, _outer._size2);
						return span2[index];
					}

					index -= _outer._size2;

					if (index < _outer._size3)
					{
						var span3 = new Span<T>(_outer._buffer3.pos, _outer._size3);
						return span3[index];
					}

					throw new IndexOutOfRangeException("Index out of range");
				}
			}
			set
			{
				unsafe
				{
					if (index < 0)
					{
						throw new IndexOutOfRangeException("Index out of range");
					}


					if (index < _outer._size1)
					{
						var span1 = new Span<T>(_outer._buffer1.pos, _outer._size1);
						span1[index] = value;
						return;
					}

					index -= _outer._size1;


					if (index < _outer._size2)
					{
						var span2 = new Span<T>(_outer._buffer2.pos, _outer._size2);
						span2[index] = value;
						return;
					}

					index -= _outer._size2;

					if (index < _outer._size3)
					{
						var span3 = new Span<T>(_outer._buffer3.pos, _outer._size3);
						span3[index] = value;
						return;
					}

					throw new IndexOutOfRangeException("Index out of range");
				}
			}
		}
		public int Length => _size;

		public IEnumerator<T> GetEnumerator() => new CombinedSpanViewEnumerator(this);
		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

		private class CombinedSpanViewEnumerator : IEnumerator<T>
		{
			private CombinedSpanView<T> _outer;
			public CombinedSpanViewEnumerator(CombinedSpanView<T> outer)
			{
				_outer = outer;
			}
			private int _index;

			public bool MoveNext()
			{
				if (_index < _outer._size)
				{
					_index++;
					return true;
				}
				return false;
			}

			public void Reset() => _index = 0;
			public T Current => _outer[_index];
			object IEnumerator.Current => Current;
			public void Dispose() { }
		}
	}
}
