using System.Collections.Generic;

namespace CHDSharpLib.Utils;

internal class ArrayPool
{
    private uint _arraySize;
    private List<byte[]> _array;
    private int _count;

    internal ArrayPool(uint arraySize)
    {
        _array = new List<byte[]>();
        _arraySize = arraySize;
        _count = 0;
    }

    internal byte[] Rent()
    {
        lock (_array)
        {
            if (_count == 0)
            {
                return new byte[_arraySize];
            }

            _count--;
            byte[] ret = _array[_count];
            _array.RemoveAt(_count);
            return ret;
        }
    }

    internal void Return(byte[] ret)
    {
        lock(_array)
        {
            _array.Add(ret);
            _count++;
        }
    }
}
