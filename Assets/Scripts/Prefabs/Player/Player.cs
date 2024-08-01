using System;
using System.Collections.Generic;
using System.Linq;
using ExtensionFunctions;
using Managers;
using Model;
using Network;
using Partials;
using UI;
using Unity.Mathematics;
using Unity.Netcode;
using Unity.VisualScripting;
using UnityEngine;
using Utils.Unity.Multiplayer.Samples.Utilities.ClientAuthority;
using VoxelEngine;
using Random = UnityEngine.Random;


namespace Prefabs.Player
{
    public class Player : NetworkBehaviour
    {
        private SceneManager _sm;

        [Header("Params")] [SerializeField] public float speed = 8f;
        [SerializeField] public float fallSpeed = 2f;
        [SerializeField] public float gravity = 9.18f;
        [SerializeField] public float jumpHeight = 1.25f;
        [SerializeField] public float maxVelocityY = 20f;
        [SerializeField] private float cameraBounceDuration;
        [SerializeField] private AnimationCurve cameraBounceCurve;
        [SerializeField] private LayerMask groundLayerMask;

        [Header("Components")] [SerializeField]
        private CharacterController characterController;

        [SerializeField] private Transform groundCheck;
        [SerializeField] private Animator bodyAnimator;
        [SerializeField] private Transform enemyWeaponContainer;
        [SerializeField] public Weapon weapon;
        [SerializeField] public AudioSource audioSource;
        [SerializeField] public Transform cameraTransform;
        [SerializeField] private Transform head;
        [SerializeField] private Transform belly;
        [SerializeField] private Ragdoll ragdoll;
        [SerializeField] private SkinnedMeshRenderer[] bodyMeshes;

        [Header("Prefabs")] [SerializeField] public List<GameObject> muzzles;
        [SerializeField] public List<GameObject> smokes;
        [SerializeField] public GameObject circleDamage;

        [Header("AudioClips")] [SerializeField]
        public AudioClip walkGeneric;

        [SerializeField] public AudioClip walkMetal;
        [SerializeField] public AudioClip walkWater;

        private Transform _transform;
        private bool _isGrounded;
        private Vector3 _velocity;
        private float _cameraBounceStart, _cameraBounceIntensity;
        private Vector3 _cameraInitialLocalPosition;
        private float _lastWalkCheck;
        [NonSerialized] public GameObject WeaponPrefab;
        private bool isDying;

        // Used to set the enemy walking animation
        private readonly NetworkVariable<bool> _isPlayerWalking = new(false,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        // Used to disable the enemy walking animation
        public readonly NetworkVariable<long> LastShot = new(0,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        // Used to animate the enemy's body tilt
        public readonly NetworkVariable<byte> CameraRotationX = new(0,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        // Used to update the enemy's weapon prefab
        public readonly NetworkVariable<NetString> EquippedWeapon = new(new NetString { Message = "" },
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public readonly NetworkVariable<PlayerStatus> Status = new(new PlayerStatus(null));

        public override void OnNetworkSpawn()
        {
            if (IsOwner)
            {
                Spawn();
                LoadStatus(Status.Value);
                Status.OnValueChanged += (_, newStatus) => LoadStatus(newStatus);
            }
            else
            {
                _isPlayerWalking.OnValueChanged += (_, newValue) =>
                {
                    var isShooting = DateTimeOffset.Now.ToUnixTimeMilliseconds() - LastShot.Value < 400;
                    bodyAnimator.SetTrigger(newValue && !isShooting
                        ? Animator.StringToHash("walk")
                        : Animator.StringToHash("idle"));
                };
                LastShot.OnValueChanged += (_, _) =>
                {
                    bodyAnimator.SetTrigger(Animator.StringToHash("idle"));
                    // _isPlayerWalking.Value = false;
                };
                EquippedWeapon.OnValueChanged += (_, newValue) =>
                {
                    print($"Player {OwnerClientId} has equipped {newValue.Message}");
                    foreach (Transform child in enemyWeaponContainer)
                        Destroy(child.gameObject);
                    var go = Resources.Load<GameObject>($"Prefabs/weapons/enemy/{newValue.Message.Value.ToUpper()}");
                    WeaponPrefab = Instantiate(go, enemyWeaponContainer).Apply(o =>
                    {
                        o.AddComponent<WeaponSway>();
                        if (newValue.Message.Value.ToUpper() == "BLOCK")
                            o.GetComponent<MeshRenderer>().material = Resources.Load<Material>(
                                $"Textures/texturepacks/blockade/Materials/blockade_{(Status.Value.BlockType.sideID + 1):D1}");
                    });
                };
                CameraRotationX.OnValueChanged += (_, newValue) =>
                {
                    var rotation = (float)(newValue - 128);
                    var headRotation = Mathf.Clamp(rotation, -50, 20f) - 20f;
                    var bellyRotation = Mathf.Clamp(rotation, -25f, 25f);
                    head.localRotation = Quaternion.Euler(headRotation, 0f, 0f);
                    belly.localRotation = Quaternion.Euler(bellyRotation, 0f, 0f);
                };
            }
        }

        // Owner only
        private void LoadStatus(PlayerStatus status)
        {
            _sm.hpHUD.SetHp(status.Hp, status.HasHelmet);
            _sm.ammoHUD.SetGrenades(status.LeftGrenades);
        }

        // Owner only
        private void Spawn()
        {
            _sm = FindObjectOfType<SceneManager>();
            _velocity = new Vector3();
            var spawnPoint = _sm.worldManager.Map.GetRandomSpawnPoint(Status.Value.Team) + Vector3.up * 2f;
            var rotation = Quaternion.Euler(0, Random.Range(-180f, 180f), 0);
            transform.SetPositionAndRotation(spawnPoint, rotation);
            GetComponent<ClientNetworkTransform>().Interpolate = true;

            // Load the body skin
            foreach (var bodyMesh in bodyMeshes)
            {
                if (bodyMesh.IsDestroyed()) continue;
                var skinName = Status.Value.Skin.GetSkinForTeam(Status.Value.Team);
                bodyMesh.material = Resources.Load<Material>($"Materials/skin/{skinName}");
            }
        }

        private void Start()
        {
            if (!IsOwner) return;
            _transform = transform;
            _cameraInitialLocalPosition = cameraTransform.localPosition;

            // Update the view distance. Render new chunks if needed.
            InvokeRepeating(nameof(UpdateChunks), 0, 1);
        }

        private void UpdateChunks() =>
            _sm.worldManager.UpdatePlayerPos(_transform.position);

        private void Update()
        {
            if (!IsOwner || isDying) return;

            // Debug
            if (Input.GetKeyDown(KeyCode.L))
                DamageClientRpc(150, "chest",
                    new NetVector3(VectorExtensions.RandomVector3(-1f, 1f).normalized), 0);

            // When the player has touched the ground, activate his jump.
            _isGrounded = Physics.CheckBox(groundCheck.position, new Vector3(0.4f, 0.25f, 0.4f), Quaternion.identity,
                groundLayerMask);
            if (_isGrounded && _velocity.y < 0)
            {
                // When the player hits the ground after a hard enough fall, start the camera bounce animation.
                if (_velocity.y < -fallSpeed * 2)
                {
                    _cameraBounceStart = Time.time;
                    _cameraBounceIntensity = 1f * Mathf.Pow(-_velocity.y / maxVelocityY, 3f);
                }

                // The vertical speed is set to a value less than 0 to get a faster fall on the next fall. 
                _velocity.y = -fallSpeed;
            }

            // Move the camera down when hitting the ground after an high fall.
            var bounceTime = (Time.time - _cameraBounceStart) / cameraBounceDuration;
            cameraTransform.transform.localPosition = _cameraInitialLocalPosition + Vector3.down *
                ((bounceTime > 1 ? 0 : cameraBounceCurve.Evaluate(bounceTime)) * _cameraBounceIntensity);

            // Handle XZ movement
            var x = Input.GetAxis("Horizontal");
            var z = Input.GetAxis("Vertical");
            var move = _transform.right * x + _transform.forward * z;
            _velocity.y -= gravity * Time.deltaTime;
            _velocity.y = Mathf.Clamp(_velocity.y, -maxVelocityY, 100);
            characterController.Move(move * (speed * Time.deltaTime * (Weapon.isAiming ? 0.66f : 1f)) +
                                     _velocity * Time.deltaTime);

            // Broadcast the walking state
            var isWalking = math.abs(x) > 0.1f || math.abs(z) > 0.1f;
            if (_isPlayerWalking.Value != isWalking)
                _isPlayerWalking.Value = isWalking;

            // Handle jump
            if (Input.GetButtonDown("Jump") && _isGrounded && !Weapon.isAiming)
                _velocity.y = Mathf.Sqrt(jumpHeight * 2f * gravity);

            // Invisible walls on map edges
            var mapSize = _sm.worldManager.Map.size;
            var pos = _transform.position;
            if (pos.x > mapSize.x || pos.y > mapSize.y ||
                pos.z > mapSize.z || pos.x > 0 || pos.y > 0 ||
                pos.z > 0)
                transform.position = new Vector3(MathF.Max(0.5f, Mathf.Min(pos.x, mapSize.x - 0.5f)),
                    MathF.Max(0.5f, Mathf.Min(pos.y, mapSize.y - 0.5f)),
                    MathF.Max(0.5f, Mathf.Min(pos.z, mapSize.z - 0.5f)));

            if (pos.y < 0.85)
                Spawn();

            // Play walk sound
            if (Time.time - _lastWalkCheck > 0.075f)
            {
                _lastWalkCheck = Time.time;
                if (_isGrounded && move.magnitude > 0.1f)
                {
                    var terrainType =
                        _sm.worldManager.GetVoxel(Vector3Int.FloorToInt(cameraTransform.position - Vector3.up * 2));
                    var hasWater =
                        _sm.worldManager.GetVoxel(Vector3Int.FloorToInt(cameraTransform.position - Vector3.up * 1))!
                            .name.Contains("water");

                    if (terrainType == null)
                        return;
                    var clip = walkGeneric;
                    if (new List<string> { "iron", "steel" }.Any(it =>
                            terrainType.name.Contains(it)))
                        clip = walkMetal;
                    if (hasWater)
                        clip = walkWater;
                    if (audioSource.clip != clip)
                        audioSource.clip = clip;
                    if (!audioSource.isPlaying)
                        audioSource.Play();
                }
                else if (audioSource.isPlaying)
                    audioSource.Pause();
            }

            // Handle inventory weapon switch
            if (weapon.WeaponModel != null)
            {
                WeaponType? weapon = null;
                if (Input.GetKeyDown(KeyCode.Alpha1) && this.weapon.WeaponModel!.Type != WeaponType.Block)
                    weapon = WeaponType.Block;
                else if (Input.GetKeyDown(KeyCode.Alpha2) && this.weapon.WeaponModel!.Type != WeaponType.Melee)
                    weapon = WeaponType.Melee;
                else if (Input.GetKeyDown(KeyCode.Alpha3) && this.weapon.WeaponModel!.Type != WeaponType.Primary)
                    weapon = WeaponType.Primary;
                else if (Input.GetKeyDown(KeyCode.Alpha4) && this.weapon.WeaponModel!.Type != WeaponType.Secondary)
                    weapon = WeaponType.Secondary;
                else if (Input.GetKeyDown(KeyCode.Alpha5) && this.weapon.WeaponModel!.Type != WeaponType.Tertiary)
                    weapon = WeaponType.Tertiary;
                if (weapon is not null)
                    this.weapon.SwitchEquipped(weapon.Value);

                if (Input.GetMouseButtonDown(1) && this.weapon.WeaponModel!.Type != WeaponType.Block &&
                    this.weapon.WeaponModel!.Type != WeaponType.Melee)
                    this.weapon.ToggleAim();
            }

            // Handle weapon reloading
            if (Input.GetKeyDown(KeyCode.R) && (weapon.WeaponModel?.IsGun ?? false))
                if (weapon.Magazine[weapon.WeaponModel!.Name] < weapon.WeaponModel.Magazine)
                    weapon.Reload();
                else weapon.audioSource.PlayOneShot(weapon.noAmmoClip);
        }

        public void SpawnWeaponEffect(WeaponType weaponType)
        {
            var mouth = WeaponPrefab.transform.Find("mouth");
            if (mouth)
            {
                Instantiate(
                    weaponType == WeaponType.Tertiary ? smokes.RandomItem() : muzzles.RandomItem(),
                    mouth).Apply(o => o.layer = LayerMask.NameToLayer(IsOwner ? "WeaponCamera" : "Default"));
                if (IsOwner)
                    SpawnWeaponEffectRpc(weapon.WeaponModel!.Type);
            }
        }

        [Rpc(SendTo.NotOwner)]
        private void SpawnWeaponEffectRpc(WeaponType weaponType) => SpawnWeaponEffect(weaponType);

        [Rpc(SendTo.Owner)]
        public void DamageClientRpc(uint damage, string bodyPart, NetVector3 direction, ulong attackerID,
            float ragdollScale = 1)
        {
            print($"{OwnerClientId} - {attackerID} has attacked {OwnerClientId} dealing {damage} damage!");
            var newStatus = Status.Value;
            newStatus.Hp -= (int)damage;

            // Update player's HP
            Status.Value = newStatus;

            // Ragdoll
            if (newStatus.Hp <= 0)
            {
                RagdollRpc((uint)(damage * ragdollScale), bodyPart, direction);
                _sm.serverManager.RespawnServerRpc();
            }

            // Spawn damage circle
            var attacker = GameObject.FindGameObjectsWithTag("Player")
                .First(it => it.GetComponent<Player>().OwnerClientId == attackerID);
            var directionToEnemy = attacker.transform.position - cameraTransform.position;
            var projectedDirection = Vector3.ProjectOnPlane(directionToEnemy, cameraTransform.up);
            var angle = Vector3.SignedAngle(cameraTransform.forward, projectedDirection, Vector3.up);
            var circleDamageGo = Instantiate(circleDamage, _sm.circleDamageContainer.transform);
            circleDamageGo.GetComponent<RectTransform>().rotation = Quaternion.Euler(0, 0, -angle);
        }

        [Rpc(SendTo.Everyone)]
        private void RagdollRpc(uint damage, string bodyPart, NetVector3 direction)
        {
            isDying = true;
            print($"{OwnerClientId} is dead!");
            if (!IsOwner)
            {
                ragdoll.ApplyForce(bodyPart,
                    Quaternion.AngleAxis(0, Vector3.up) * direction.ToVector3.normalized *
                    math.clamp(damage * 2, 10, 200));
                gameObject.AddComponent<Destroyable>().lifespan = 10;
            }
            else
            {
                GetComponent<CharacterController>().enabled = false;
                GetComponent<CapsuleCollider>().enabled = true;
                GetComponentInChildren<CameraMovement>().enabled = false;
                GetComponentInChildren<Weapon>().enabled = false;
                GetComponentInChildren<WeaponSway>().enabled = false;
                transform.Find("WeaponCamera").gameObject.SetActive(false);
                gameObject.AddComponent<Rigidbody>().Apply(rb =>
                    rb.AddForceAtPosition(direction.ToVector3 * (damage * 3f),
                        _transform.position + Vector3.up * 0.5f));
            }
        }
    }
}