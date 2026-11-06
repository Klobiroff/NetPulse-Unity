using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetPulse
{
    internal sealed class OperationScheduler
    {
        private readonly List<Func<bool>> _steps = new();
        private readonly List<Func<bool>> _incoming = new();
        private bool _running;

        public int Count => _steps.Count + _incoming.Count;

        public void Add(Func<bool> step)
        {
            if (_running)
            {
                _incoming.Add(step);
                return;
            }

            _steps.Add(step);
        }

        public void Run()
        {
            if (_steps.Count == 0)
            {
                return;
            }

            _running = true;
            var index = 0;
            while (index < _steps.Count)
            {
                var finished = false;
                try
                {
                    finished = _steps[index]();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    finished = true;
                }

                if (finished)
                {
                    _steps.RemoveAt(index);
                    continue;
                }

                index++;
            }

            _running = false;
            if (_incoming.Count > 0)
            {
                _steps.AddRange(_incoming);
                _incoming.Clear();
            }
        }
    }
}
