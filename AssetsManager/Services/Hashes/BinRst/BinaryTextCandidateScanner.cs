using System;
using System.Text;

namespace AssetsManager.Services.Hashes
{
    internal sealed class BinaryTextCandidateScanner
    {
        private const int MaximumCandidateLength = 512;
        private readonly Action<string> _check;
        private readonly byte[] _candidate = new byte[MaximumCandidateLength];
        private readonly byte[] _function = new byte[MaximumCandidateLength];
        private readonly byte[] _history = new byte[4];
        private readonly byte[] _startPrefix = new byte[4];
        private int _candidateLength;
        private int _functionLength;
        private int _historyLength;
        private int _startPrefixLength;
        private int _functionState;
        private bool _candidateOverflow;
        private bool _functionOverflow;

        internal BinaryTextCandidateScanner(Action<string> check) =>
            _check = check ?? throw new ArgumentNullException(nameof(check));

        internal void Append(ReadOnlySpan<byte> data)
        {
            foreach (byte value in data)
            {
                ProcessFunctionByte(value);
                if (IsCandidateByte(value))
                {
                    if (_candidateLength == 0 && !_candidateOverflow)
                    {
                        _startPrefixLength = _historyLength;
                        Array.Copy(_history, _startPrefix, _historyLength);
                    }
                    if (_candidateLength < MaximumCandidateLength && !_candidateOverflow)
                        _candidate[_candidateLength++] = value;
                    else
                        _candidateOverflow = true;
                }
                else
                {
                    EmitCandidate();
                }
                PushHistory(value);
            }
        }

        internal void Complete()
        {
            EmitCandidate();
            EmitFunction();
            _functionState = 0;
        }

        private void ProcessFunctionByte(byte value)
        {
            switch (_functionState)
            {
                case 0:
                    if (value == (byte)'@') _functionState = 1;
                    break;
                case 1:
                    if (value == (byte)'V')
                    {
                        _functionState = 2;
                        _functionLength = 0;
                        _functionOverflow = false;
                    }
                    else
                    {
                        _functionState = value == (byte)'@' ? 1 : 0;
                    }
                    break;
                default:
                    if (value == (byte)'@')
                    {
                        EmitFunction();
                        _functionState = 1;
                    }
                    else if (IsCandidateByte(value))
                    {
                        if (_functionLength < MaximumCandidateLength && !_functionOverflow)
                            _function[_functionLength++] = value;
                        else
                            _functionOverflow = true;
                    }
                    else
                    {
                        EmitFunction();
                        _functionState = 0;
                    }
                    break;
            }
        }

        private void EmitCandidate()
        {
            if (!_candidateOverflow && _candidateLength >= 5)
            {
                CheckBytes(_candidate.AsSpan(0, _candidateLength));
                int declared = ReadDeclaredLength();
                if (declared is >= 5 and <= MaximumCandidateLength && declared < _candidateLength)
                    CheckBytes(_candidate.AsSpan(0, declared));
            }
            _candidateLength = 0;
            _candidateOverflow = false;
            _startPrefixLength = 0;
        }

        private void EmitFunction()
        {
            if (!_functionOverflow && _functionLength >= 3)
                CheckBytes(_function.AsSpan(0, _functionLength));
            _functionLength = 0;
            _functionOverflow = false;
        }

        private int ReadDeclaredLength()
        {
            if (_startPrefixLength < 2) return -1;
            int last = _startPrefixLength - 1;
            int declared = _startPrefix[last - 1] | _startPrefix[last] << 8;
            if (declared == 0 && _startPrefixLength == 4)
                declared = _startPrefix[0] | _startPrefix[1] << 8 |
                    _startPrefix[2] << 16 | _startPrefix[3] << 24;
            return declared;
        }

        private void CheckBytes(ReadOnlySpan<byte> bytes)
        {
            string candidate = Encoding.ASCII.GetString(bytes).Trim();
            if (candidate.Length >= 3) _check(candidate);
        }

        private void PushHistory(byte value)
        {
            if (_historyLength < _history.Length)
            {
                _history[_historyLength++] = value;
                return;
            }
            _history[0] = _history[1];
            _history[1] = _history[2];
            _history[2] = _history[3];
            _history[3] = value;
        }

        private static bool IsCandidateByte(byte value) => value is >= (byte)'0' and <= (byte)'9' or
            >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or
            (byte)'_' or (byte)'.' or (byte)' ' or (byte)'/' or (byte)'-';
    }
}
