using UnityEngine;

[RequireComponent(typeof(Animator))]
public class SourceHandIKTest : MonoBehaviour
{
    [Range(0f, 1f)]
    public float weight = 1f;

    private Animator animator;

    private void Awake()
    {
        animator = GetComponent<Animator>();
    }

    private void OnAnimatorIK(int layerIndex)
    {
        if (layerIndex != 1) return;

        animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, weight);
        animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, weight);

        animator.SetIKPositionWeight(AvatarIKGoal.RightHand, weight);
        animator.SetIKRotationWeight(AvatarIKGoal.RightHand, weight);
    }
}