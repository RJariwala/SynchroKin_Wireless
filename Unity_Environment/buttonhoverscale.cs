using UnityEngine;
using UnityEngine.EventSystems;

public class ButtonHoverScale : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    private Vector3 originalScale;
    public float scaleFactor = 1.05f; // Changes how much it expands. 1.05 is a 5% increase.

    void Start()
    {
        // Remember the starting size
        originalScale = transform.localScale;
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        // Expand when the mouse enters
        transform.localScale = originalScale * scaleFactor;
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        // Shrink back to normal when the mouse leaves
        transform.localScale = originalScale;
    }
}