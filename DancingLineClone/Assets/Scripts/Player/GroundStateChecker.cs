using System;
using System.Collections;
using UnityEngine;

namespace Player
{
    /// <summary>
    /// Ground checking happen here if player on Ground (Can move) or Non-Ground (Can't move and cause defeat)
    /// </summary>
    public class GroundStateChecker : MonoBehaviour
    {
        private bool _onGround = true;
        private bool _onNonGround;
        private WaitForSeconds _waitForSecondsGroundCheckInterval;
        private Coroutine _groundCheckCoroutine;
        
        //If ground change, and it's on ground it will be true, otherwise false
        public static event Action<bool> OnGroundChange;
        public static event Action<bool> OnNonGroundChange;
        [SerializeField] private float checkInterval = 0.01f;
        private Rigidbody _playerRigidbody;
        [SerializeField] private float raycastDistance = 1;
        [SerializeField] private LayerMask groundLayer;
        [SerializeField] private LayerMask nonGroundLayer;


        private void Awake()
        {
            _waitForSecondsGroundCheckInterval = new WaitForSeconds(checkInterval);
            _playerRigidbody = GetComponent<Rigidbody>();
            BeginGroundCheck();
        }

        /// <summary>
        /// Making player only can move, change direction on Ground Layer with Raycast and <see cref="groundLayer"/> by masking
        /// </summary>
        private IEnumerator GroundCheckIEnumerator()
        {
            while (true)
            {
                yield return _waitForSecondsGroundCheckInterval;
                //BoxCast recommend by AI. Explore this if this is works and how it works.
                if (Physics.BoxCast(transform.position, new Vector3(0.45f, 0.1f, 0.45f), Vector3.down, transform.rotation, raycastDistance, groundLayer))
                {
                    if (!_onGround)
                    {
                        _onGround = true;
                        GravityDisable();
                        OnGroundChange?.Invoke(_onGround);
                    }
                }
                else
                {
                    if (_onGround)
                    {
                        _onGround = false;
                        GravityEnable();
                        OnGroundChange?.Invoke(_onGround);
                    }
                }
            }
        }
        
        private void BeginGroundCheck()
        {
            _groundCheckCoroutine = StartCoroutine(GroundCheckIEnumerator());
        }

        private void StopGroundCheck()
        {
            if (_groundCheckCoroutine != null)
            {
                StopCoroutine(_groundCheckCoroutine);
            }
        }

        private void GravityEnable()
        {
            _playerRigidbody.useGravity = true;
        }

        private void GravityDisable()
        {
            _playerRigidbody.useGravity = false;
        }

        /// <summary>
        /// Checks if player touching to non-ground area where it's usually end up Game Over
        /// </summary>
        private void OnCollisionEnter(Collision other)
        {
            //TODO Explore this what is that bitmask comparise
            //Turn layer 7 into 128 for the comparison
            if (1 << other.gameObject.layer == nonGroundLayer.value)
            {
                _onNonGround = true;
                OnNonGroundChange?.Invoke(_onNonGround);
            }
        }

        private void OnCollisionExit(Collision other)
        {
            if (1 << other.gameObject.layer == nonGroundLayer.value)
            {
                _onNonGround = false;
                OnNonGroundChange?.Invoke(_onNonGround);
            }
        }
    }
}