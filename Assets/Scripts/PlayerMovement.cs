using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMovement : MonoBehaviour
{
    private CharacterController controller;
    private InputAction moveAction;
    private InputAction jumpAction;
    private float verticalVelocity;
    private Animator animator;
    private InputAction sprintAction;

    const float PlayerSpeed = 2;
    const float SprintSpeed = 5f;
    const float Gravity = -9.81f;
    const float TurnSpeed = 5760; // degrees per second

    const float JumpHeight = 0.5f; // in meters

    void Start()
    {
        // Lock and Hide the curson on Game Tab
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        controller = GetComponent<CharacterController>();
        moveAction = InputSystem.actions.FindAction("Move");
        sprintAction = InputSystem.actions.FindAction("Sprint");
        jumpAction = InputSystem.actions.FindAction("Jump");
        animator = GetComponentInChildren<Animator>();
    }


    // OLD Input Manager (legacy)

    // float horizontal = Input.GetAxis("Horizontal"); // A/D or arrows, -1 to 1
    // float vertical = Input.GetAxis("Vertical");     // W/S or arrows, -1 to 1
    //
    // Vector3 movement = new Vector3(horizontal, -9.81f, vertical);
    // controller.Move(movement * Time.deltaTime * PlayerSpeed);

    void Update()
    {
        Vector2 input = moveAction.ReadValue<Vector2>();

        // Camera's forward/right flattened on the ground, so looking down doesn't slow you down
        Transform cam = Camera.main.transform;
        Vector3 camForward = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
        Vector3 camRight = Vector3.ProjectOnPlane(cam.right, Vector3.up).normalized;
        Vector3 direction = camForward * input.y + camRight * input.x;
        Vector3 movement = Vector3.zero;

        bool hasMoveInput = direction.sqrMagnitude > 0.01f;
        bool hasJumpInput = jumpAction.WasPressedThisFrame();
        bool isSprinting = sprintAction.IsPressed();

        if (hasMoveInput)
        {
            direction.Normalize();

            // Calculate the desired rotation
            Quaternion targetRotation = Quaternion.LookRotation(direction);

            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, TurnSpeed * Time.deltaTime);

            if (Quaternion.Angle(transform.rotation, targetRotation) <= 1f)
                movement = direction * (isSprinting ? SprintSpeed : PlayerSpeed);
        }

        if (controller.isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f;

        // Jump
        if (controller.isGrounded && hasJumpInput)
            verticalVelocity = Mathf.Sqrt(JumpHeight * -2f * Gravity);

        verticalVelocity += Gravity * Time.deltaTime;
        movement.y = verticalVelocity;

        controller.Move(movement * Time.deltaTime);

        // Animations
        animator.SetBool("Move", hasMoveInput);
        animator.SetBool("Sprint", isSprinting);
        animator.SetBool("Jump", !controller.isGrounded);
    }
}
