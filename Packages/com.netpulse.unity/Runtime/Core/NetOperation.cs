using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace NetPulse
{
    public readonly struct NetError
    {
        public NetError(short returnCode, string message)
        {
            ReturnCode = returnCode;
            Message = message ?? string.Empty;
        }

        public short ReturnCode { get; }

        public string Message { get; }

        public override string ToString() => $"[{ReturnCode}] {Message}";
    }

    public sealed class NetPulseException : Exception
    {
        public NetPulseException(short returnCode, string message) : base(message)
        {
            ReturnCode = returnCode;
        }

        public short ReturnCode { get; }
    }

    public class NetOperation : CustomYieldInstruction
    {
        private NetError? _error;
        private Action<NetOperation> _completed;
        private Action _cancelHandler;

        public bool IsDone { get; private set; }

        public bool IsCanceled { get; private set; }

        public bool IsSuccess => IsDone && !_error.HasValue && !IsCanceled;

        public bool HasError => _error.HasValue;

        public NetError Error => _error ?? default;

        public override bool keepWaiting => !IsDone;

        public event Action<NetOperation> Completed
        {
            add
            {
                if (IsDone)
                {
                    value?.Invoke(this);
                    return;
                }

                _completed += value;
            }
            remove => _completed -= value;
        }

        public static NetOperation Succeeded()
        {
            var op = new NetOperation();
            op.Complete();
            return op;
        }

        public static NetOperation Failed(short code, string message)
        {
            var op = new NetOperation();
            op.Fail(code, message);
            return op;
        }

        public NetOperationAwaiter GetAwaiter() => new(this);

        public void Cancel()
        {
            if (IsDone)
            {
                return;
            }

            IsCanceled = true;
            IsDone = true;
            var handler = _cancelHandler;
            _cancelHandler = null;
            handler?.Invoke();
            Raise();
        }

        internal void SetCancelHandler(Action handler) => _cancelHandler = handler;

        internal virtual void Complete()
        {
            if (IsDone)
            {
                return;
            }

            _cancelHandler = null;
            IsDone = true;
            Raise();
        }

        internal virtual void Fail(short code, string message)
        {
            if (IsDone)
            {
                return;
            }

            _error = new NetError(code, message);
            _cancelHandler = null;
            IsDone = true;
            Raise();
        }

        private void Raise()
        {
            var callback = _completed;
            _completed = null;
            if (callback == null)
            {
                return;
            }

            foreach (var handler in callback.GetInvocationList())
            {
                try
                {
                    ((Action<NetOperation>)handler)(this);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }
    }

    public class NetOperation<T> : NetOperation
    {
        public T Result { get; private set; }

        public static NetOperation<T> Succeeded(T result)
        {
            var op = new NetOperation<T>();
            op.Complete(result);
            return op;
        }

        public new static NetOperation<T> Failed(short code, string message)
        {
            var op = new NetOperation<T>();
            op.Fail(code, message);
            return op;
        }

        public new NetOperationAwaiter<T> GetAwaiter() => new(this);

        internal void Complete(T result)
        {
            if (IsDone)
            {
                return;
            }

            Result = result;
            Complete();
        }
    }

    public readonly struct NetOperationAwaiter : ICriticalNotifyCompletion
    {
        private readonly NetOperation _operation;

        internal NetOperationAwaiter(NetOperation operation)
        {
            _operation = operation;
        }

        public bool IsCompleted => _operation.IsDone;

        public void OnCompleted(Action continuation) => _operation.Completed += _ => continuation();

        public void UnsafeOnCompleted(Action continuation) => _operation.Completed += _ => continuation();

        public void GetResult()
        {
            if (_operation.IsCanceled)
            {
                throw new OperationCanceledException();
            }

            if (_operation.HasError)
            {
                throw new NetPulseException(_operation.Error.ReturnCode, _operation.Error.Message);
            }
        }
    }

    public readonly struct NetOperationAwaiter<T> : ICriticalNotifyCompletion
    {
        private readonly NetOperation<T> _operation;

        internal NetOperationAwaiter(NetOperation<T> operation)
        {
            _operation = operation;
        }

        public bool IsCompleted => _operation.IsDone;

        public void OnCompleted(Action continuation) => _operation.Completed += _ => continuation();

        public void UnsafeOnCompleted(Action continuation) => _operation.Completed += _ => continuation();

        public T GetResult()
        {
            if (_operation.IsCanceled)
            {
                throw new OperationCanceledException();
            }

            if (_operation.HasError)
            {
                throw new NetPulseException(_operation.Error.ReturnCode, _operation.Error.Message);
            }

            return _operation.Result;
        }
    }
}
