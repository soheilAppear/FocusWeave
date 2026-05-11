using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

namespace OculusSampleFramework
{
    public class PlayerControllerRestView : MonoBehaviour
    {
        // Transform component that defines the position to reset the player to
        [SerializeField] Transform resetTransform;

        // Reference to the player GameObject
        [SerializeField] GameObject player;

        // Reference to the player head camera
        [SerializeField] Camera playerHead;

        // Audio clip to play on start
        public AudioClip clip;

        // Audio source component for playing the audio clip
        public AudioSource source;

        // Start function is called before the first frame update
        void Start()
        {
            // Get the AudioSource component from the game object
            source = GetComponent<AudioSource>();

            // Play the audio clip once
            source.PlayOneShot(clip);
        }

        // Function to reset the player's position
        public void ResetPosition()
        {
            // Calculate the difference in y rotation between the reset position and player head
            var rotationAngleY = resetTransform.rotation.eulerAngles.y - playerHead.transform.rotation.eulerAngles.y;

            // Rotate the player by the difference in rotation
            player.transform.Rotate(0, rotationAngleY, 0);

            // Calculate the difference in position between the reset position and player head
            var distanceDiff = resetTransform.position - playerHead.transform.position;

            // Move the player by the difference in position
            player.transform.position += distanceDiff;

            // Translate the player to the height of the player head
            player.transform.Translate(0f, (float)playerHead.transform.localPosition.y, 0f);

            // Log the reset position, player head position, and height of player head
            Debug.Log("resetTransform.position : " + resetTransform.position);
            Debug.Log("playerHead.transform.position : "+ playerHead.transform.position);
            Debug.Log("localPosition Height is : " + playerHead.transform.localPosition.y);
        }

        // Update function is called once per frame
        void Update()
        {
            // Check if the primary index trigger on the Oculus Touch controller is down
            if ((OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.Touch))) 
            {
                // Call the ResetPosition function
                ResetPosition();
            }
        }
    }
}  