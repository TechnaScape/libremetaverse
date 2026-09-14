namespace LibreMetaverse
{
    public class AgentSettings
    {
        public bool SendUpdates = true;
        public bool SendUpdatesRegularly = true;
        public bool SendAppearance = true;
        public bool SendThrottle = true;
        public bool DisableUpdateDuplicateCheck = true;
        public bool MultipleSims = false;

        /// <summary>
        /// Answer the stand-up, pre-jump and landing animations with AGENT_CONTROL_FINISH_ANIM the
        /// moment the simulator starts them. The Second Life viewer sends it when the animation
        /// FINISHES playing (LLAgent::onAnimStop), and not while jump is held; a client that drives
        /// its own AgentUpdates and does the same should turn this off.
        /// </summary>
        public bool FinishAnimationsOnStart = true;
    }
}
