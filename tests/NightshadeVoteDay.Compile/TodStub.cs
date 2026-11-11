public class TOD_CycleParameters
{
    public float Hour;
    public int Year;
    public int Month;
    public int Day;
}

public class TOD_Sky : UnityEngine.MonoBehaviour
{
    public static TOD_Sky Instance;
    public TOD_CycleParameters Cycle;
}
