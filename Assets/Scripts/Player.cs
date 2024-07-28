using System;
using System.Collections.Generic;
using System.Linq;
using ExtensionFunctions;
using Managers;
using Model;
using Network;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Netcode;
using Unity.VisualScripting;
using UnityEngine;
using VoxelEngine;
using Random = UnityEngine.Random;


public class Player : NetworkBehaviour
{
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
    [SerializeField] private WeaponManager weaponManager;
    [SerializeField] public AudioSource audioSource;
    [SerializeField] private Transform cameraTransform;
    [SerializeField] private Transform head;
    [SerializeField] private Transform belly;

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
    [NonSerialized] public GameObject weaponPrefab;

    private readonly NetworkVariable<bool> _isPlayerWalking = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public readonly NetworkVariable<long> lastShot = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public readonly NetworkVariable<byte> cameraRotationX = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);


    public readonly NetworkVariable<Message> equipped = new(new Message { message = "" },
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);

    public struct Message : INetworkSerializable
    {
        public FixedString32Bytes message;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref message);
        }
    }

    public override void OnNetworkSpawn()
    {
        Spawn();

        if (!IsOwner)
        {
            _isPlayerWalking.OnValueChanged += (_, newValue) =>
            {
                var isShooting = DateTimeOffset.Now.ToUnixTimeMilliseconds() - lastShot.Value < 400;
                bodyAnimator.SetTrigger(newValue && !isShooting
                    ? Animator.StringToHash("walk")
                    : Animator.StringToHash("idle"));
            };
            lastShot.OnValueChanged += (_, _) =>
            {
                bodyAnimator.SetTrigger(Animator.StringToHash("idle"));
                _isPlayerWalking.Value = false;
            };

            equipped.OnValueChanged += (_, newValue) =>
            {
                print($"Player {OwnerClientId} has equipped {newValue.message}");
                foreach (Transform child in enemyWeaponContainer)
                    Destroy(child.gameObject);
                var go = Resources.Load<GameObject>($"Prefabs/weapons/enemy/{newValue.message.Value.ToUpper()}");
                weaponPrefab = Instantiate(go, enemyWeaponContainer).Apply(o =>
                {
                    o.AddComponent<WeaponSway>();
                    if (newValue.message.Value.ToUpper() == "BLOCK")
                        o.GetComponent<MeshRenderer>().material = Resources.Load<Material>(
                            $"Textures/texturepacks/blockade/Materials/blockade_{(InventoryManager.Instance.BlockType.sideID + 1):D1}");
                });
            };

            cameraRotationX.OnValueChanged += (_, newValue) =>
            {
                var rotation = (float)(newValue - 128);
                var headRotation = Mathf.Clamp(rotation, -50, 20f) - 20f;
                var bellyRotation = Mathf.Clamp(rotation, -25f, 25f);
                head.localRotation = Quaternion.Euler(0f, 0f, headRotation);
                belly.localRotation = Quaternion.Euler(0f, 0f, bellyRotation);
            };
        }
    }

    private void Spawn()
    {
        _velocity = new();
        var spawnPoint = WorldManager.instance.map.GetRandomSpawnPoint(InventoryManager.Instance.team) +
                         Vector3.up * 1.25f;
        var rotation = Quaternion.Euler(0, Random.Range(-180f, 180f), 0);
        transform.SetPositionAndRotation(spawnPoint, rotation);
    }

    private void Start()
    {
        if (!IsOwner) return;
        _transform = transform;
        _cameraInitialLocalPosition = cameraTransform.localPosition;
        WorldManager.instance.UpdatePlayerPos(_transform.position);
    }

    private void Update()
    {
        if (!IsOwner) return;

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
        characterController.Move(move * (speed * Time.deltaTime * (WeaponManager.isAiming ? 0.66f : 1f)) +
                                 _velocity * Time.deltaTime);

        // Broadcast the walking state
        var isWalking = math.abs(x) > 0.1f || math.abs(z) > 0.1f;
        if (_isPlayerWalking.Value != isWalking)
            _isPlayerWalking.Value = isWalking;

        // Handle jump
        if (Input.GetButtonDown("Jump") && _isGrounded && !WeaponManager.isAiming)
            _velocity.y = Mathf.Sqrt(jumpHeight * 2f * gravity);

        // Invisible walls on map edges
        var mapSize = WorldManager.instance.map.size;
        var pos = _transform.position;
        if (pos.x > mapSize.x || pos.y > mapSize.y ||
            pos.z > mapSize.z || pos.x > 0 || pos.y > 0 ||
            pos.z > 0)
            transform.position = new Vector3(MathF.Max(0.5f, Mathf.Min(pos.x, mapSize.x - 0.5f)),
                MathF.Max(0.5f, Mathf.Min(pos.y, mapSize.y - 0.5f)),
                MathF.Max(0.5f, Mathf.Min(pos.z, mapSize.z - 0.5f)));

        if (pos.y < -2)
            Spawn();

        // Update the view distance. Render new chunks if needed.
        WorldManager.instance.UpdatePlayerPos(pos);

        // Play walk sound
        if (Time.time - _lastWalkCheck > 0.075f)
        {
            _lastWalkCheck = Time.time;
            if (_isGrounded && move.magnitude > 0.1f)
            {
                var terrainType =
                    WorldManager.instance.GetVoxel(Vector3Int.FloorToInt(cameraTransform.position - Vector3.up * 2));
                var hasWater =
                    WorldManager.instance.GetVoxel(Vector3Int.FloorToInt(cameraTransform.position - Vector3.up * 1))!
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
        if (weaponManager.WeaponModel != null)
        {
            WeaponType? weapon = null;
            if (Input.GetKeyDown(KeyCode.Alpha1) && weaponManager.WeaponModel!.type != WeaponType.Block)
                weapon = WeaponType.Block;
            else if (Input.GetKeyDown(KeyCode.Alpha2) && weaponManager.WeaponModel!.type != WeaponType.Melee)
                weapon = WeaponType.Melee;
            else if (Input.GetKeyDown(KeyCode.Alpha3) && weaponManager.WeaponModel!.type != WeaponType.Primary)
                weapon = WeaponType.Primary;
            else if (Input.GetKeyDown(KeyCode.Alpha4) && weaponManager.WeaponModel!.type != WeaponType.Secondary)
                weapon = WeaponType.Secondary;
            else if (Input.GetKeyDown(KeyCode.Alpha5) && weaponManager.WeaponModel!.type != WeaponType.Tertiary)
                weapon = WeaponType.Tertiary;
            if (weapon != null)
            {
                weaponManager.SwitchEquipped(weapon.Value);
                equipped.Value = new Message { message = weaponManager.WeaponModel.name };
            }

            if (Input.GetMouseButtonDown(1) && weaponManager.WeaponModel!.type != WeaponType.Block &&
                weaponManager.WeaponModel!.type != WeaponType.Melee)
                weaponManager.ToggleAim();
        }
    }

    public void SpawnWeaponEffect(WeaponType weaponType)
    {
        var mouth = weaponPrefab.transform.Find("mouth");
        if (mouth)
        {
            Instantiate(
                weaponType == WeaponType.Tertiary ? smokes.RandomItem() : muzzles.RandomItem(),
                mouth).Apply(o => o.layer = LayerMask.NameToLayer(IsOwner ? "WeaponCamera" : "Default"));
            if (IsOwner)
                SpawnWeaponEffectRpc(weaponManager.WeaponModel!.type);
        }
    }

    [Rpc(SendTo.NotOwner)]
    private void SpawnWeaponEffectRpc(WeaponType weaponType) => SpawnWeaponEffect(weaponType);


    [Rpc(SendTo.Owner)]
    public void DamageClientRpc(uint damage, ulong attackerID)
    {
        print($"{OwnerClientId} - {attackerID} has attacked {OwnerClientId} dealing {damage} damage!");
        InventoryManager.Instance.hp -= damage;
        var circleDamageContainer = GameObject.FindWithTag("CircleDamageContainer");
        var attacker = GameObject.FindGameObjectsWithTag("Player")
            .First(it => it.GetComponent<Player>().OwnerClientId == attackerID);

        var directionToEnemy = attacker.transform.position - cameraTransform.position;
        var projectedDirection = Vector3.ProjectOnPlane(directionToEnemy, cameraTransform.up);
        var angle = Vector3.SignedAngle(cameraTransform.forward, projectedDirection, Vector3.up);

        var circleDamageGo = Instantiate(circleDamage, circleDamageContainer.transform);
        circleDamageGo.GetComponent<RectTransform>().rotation = Quaternion.Euler(0, 0, -angle);
    }
}