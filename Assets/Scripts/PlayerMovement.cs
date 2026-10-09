using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMovement : MonoBehaviour
{
    private CharacterController controller;
    private InputAction moveAction;

    const float PlayerSpeed = 5;
    const float Gravity = -9.81f;
    const float TurnSpeed = 5760; // degrees per second
    private float verticalVelocity;

    void Start()
    {
        controller = GetComponent<CharacterController>();
        moveAction = InputSystem.actions.FindAction("Move");
    }


    // OLD Input Manager (legacy)

    // float horizontal = Input.GetAxis("Horizontal"); // A/D or arrows, -1 to 1
    // float vertical = Input.GetAxis("Vertical");     // W/S or arrows, -1 to 1
    //
    // Vector3 movement = new Vector3(horizontal, -9.81f, vertical);
    // controller.Move(movement * Time.deltaTime * PlayerSpeed);

    void Update()
    {
        // Read player input
        Vector2 input = moveAction.ReadValue<Vector2>();
        Vector3 direction = new Vector3(input.x, 0f, input.y);
        Vector3 movement = Vector3.zero;

        if (direction.sqrMagnitude > 0.01f)
        {
            direction.Normalize();

            // Calculate the desired rotation
            Quaternion targetRotation = Quaternion.LookRotation(direction);

            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, TurnSpeed * Time.deltaTime);

            if (Quaternion.Angle(transform.rotation, targetRotation) <= 1f)
                movement = direction * PlayerSpeed;
        }

        if (controller.isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f;

        verticalVelocity += Gravity * Time.deltaTime;
        movement.y = verticalVelocity;

        controller.Move(movement * Time.deltaTime);
    }
}
