using UnityEngine;
using UnityEngine.SceneManagement;

public class MenuActionsScript : MonoBehaviour
{

    public void LoadOldInputScene()
    {
        SceneManager.LoadScene("NewInput");
    }

    public void QuitGame()
    {
        Application.Quit();
    }
}
