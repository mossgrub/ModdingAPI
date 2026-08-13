using GlobalEnums;
using UnityEngine;
using UnityEngine.UI;

#pragma warning disable 1591

namespace Modding
{
    public class ModVersionDraw : MonoBehaviour
    {
        private static GUIStyle style = new GUIStyle(GUIStyle.none);

        private bool isVisible = true;

        private float currentAlpha = 1f;

        private Coroutine fadeCoroutine;

        public string drawString;

        private void Start()
        {
            style.normal.textColor = Color.white;
            style.alignment = TextAnchor.UpperLeft;
            style.padding = new RectOffset(5, 5, 5, 5);

            if (!ModManagerSettings.ModListDisplay)
            {
                isVisible = false;
                currentAlpha = 0f;
            }
        }

        public void OnGUI()
        {
            if (UIManager.instance == null)
            {
                return;
            }

            if (currentAlpha <= 0f)
            {
                return;
            }

            if (drawString != null &&
               (UIManager.instance.uiState == UIState.MAIN_MENU_HOME || UIManager.instance.uiState == UIState.PAUSED))
            {
                Color originalColor = style.normal.textColor;
                style.normal.textColor = new Color(originalColor.r, originalColor.g, originalColor.b, currentAlpha);
                GUI.Label(new Rect(0, 0, Screen.width, Screen.height), drawString, style);
                style.normal.textColor = originalColor;
            }
        }

        public void SetVisible(bool visible, float fadeDuration = 0.25f)
        {
            isVisible = visible;

            if (fadeCoroutine != null)
            {
                StopCoroutine(fadeCoroutine);
            }

            fadeCoroutine = StartCoroutine(FadeToTarget(fadeDuration));
        }

        private System.Collections.IEnumerator FadeToTarget(float duration)
        {
            float targetAlpha = isVisible ? 1f : 0f;
            float startAlpha = currentAlpha;
            float elapsed = 0f;

            if (duration <= 0f)
            {
                currentAlpha = targetAlpha;
                yield break;
            }

            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                currentAlpha = Mathf.Lerp(startAlpha, targetAlpha, elapsed / duration);
                yield return null;
            }

            currentAlpha = targetAlpha;
            fadeCoroutine = null;
        }
    }
}
