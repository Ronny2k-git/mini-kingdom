using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMovement : MonoBehaviour
{
    private CharacterController controller;
    private InputAction moveAction;

    const float PlayerSpeed = 5;
    const float Gravity = -9.81f;

    void Start()
    {
        controller = GetComponent<CharacterController>();
        moveAction = InputSystem.actions.FindAction("Move");
    }

    // Executed each frame
    void Update()
    {
        // NEW Input System: the action returns a Vector2 (x = left/right, y = forward/back)
        Vector2 input = moveAction.ReadValue<Vector2>();
        Vector3 movement = new Vector3(input.x, 0, input.y) * PlayerSpeed;

        // Gravity is kept apart so it isn't multiplied by PlayerSpeed
        movement.y = Gravity;

        // Deltatime is necessary to move the character according to the FPS
        controller.Move(movement * Time.deltaTime);

        // OLD Input Manager (legacy) - only works if Project Settings > Player >

        // float horizontal = Input.GetAxis("Horizontal"); // A/D or arrows, -1 to 1
        // float vertical = Input.GetAxis("Vertical");     // W/S or arrows, -1 to 1
        //
        // Vector3 movement = new Vector3(horizontal, -9.81f, vertical);
        // controller.Move(movement * Time.deltaTime * PlayerSpeed);
    }
}
