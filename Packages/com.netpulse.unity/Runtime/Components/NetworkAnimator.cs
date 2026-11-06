using System.Collections.Generic;
using UnityEngine;

namespace NetPulse
{
    [AddComponentMenu("NetPulse/Network Animator")]
    [DisallowMultipleComponent]
    public sealed partial class NetworkAnimator : NetworkBehaviour
    {
        [SerializeField] private Animator _animator;
        [SerializeField] private bool _syncLayerWeights = false;

        private readonly List<AnimatorControllerParameter> _parameters = new();

        public Animator Animator => _animator;

        public void SetTrigger(string triggerName)
        {
            if (!IsMine)
            {
                return;
            }

            var hash = Animator.StringToHash(triggerName);
            _animator.SetTrigger(hash);
            if (IsSpawned && Pulse.IsInRoom)
            {
                Rpc.RpcSetTrigger(hash);
            }
        }

        public void ResetTrigger(string triggerName) => _animator.ResetTrigger(triggerName);

        public override void OnSerialize(NetStream stream)
        {
            if (_animator == null)
            {
                return;
            }

            foreach (var parameter in _parameters)
            {
                SerializeParameter(stream, parameter);
            }

            if (!_syncLayerWeights)
            {
                return;
            }

            for (var i = 1; i < _animator.layerCount; i++)
            {
                var weight = _animator.GetLayerWeight(i);
                stream.Serialize(ref weight);
                if (stream.IsReading)
                {
                    _animator.SetLayerWeight(i, weight);
                }
            }
        }

        [NetRpc(RpcTarget.Others)]
        private void RpcSetTrigger(int hash)
        {
            if (_animator != null)
            {
                _animator.SetTrigger(hash);
            }
        }

        private void Awake()
        {
            if (_animator == null)
            {
                TryGetComponent(out _animator);
            }

            if (_animator == null)
            {
                return;
            }

            foreach (var parameter in _animator.parameters)
            {
                if (parameter.type != AnimatorControllerParameterType.Trigger)
                {
                    _parameters.Add(parameter);
                }
            }
        }

        private void SerializeParameter(NetStream stream, AnimatorControllerParameter parameter)
        {
            switch (parameter.type)
            {
                case AnimatorControllerParameterType.Bool:
                    var boolValue = _animator.GetBool(parameter.nameHash);
                    stream.Serialize(ref boolValue);
                    if (stream.IsReading)
                    {
                        _animator.SetBool(parameter.nameHash, boolValue);
                    }

                    break;
                case AnimatorControllerParameterType.Int:
                    var intValue = _animator.GetInteger(parameter.nameHash);
                    stream.Serialize(ref intValue);
                    if (stream.IsReading)
                    {
                        _animator.SetInteger(parameter.nameHash, intValue);
                    }

                    break;
                case AnimatorControllerParameterType.Float:
                    var floatValue = _animator.GetFloat(parameter.nameHash);
                    stream.Serialize(ref floatValue);
                    if (stream.IsReading)
                    {
                        _animator.SetFloat(parameter.nameHash, floatValue);
                    }

                    break;
            }
        }
    }
}
