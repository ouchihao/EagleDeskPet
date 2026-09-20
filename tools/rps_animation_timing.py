"""Shared offline RPS V2 timing for construction and legacy outfit audits."""

# The canonical seam gets enough recovery time without compressing the
# fingers into two or three samples. Four living poses preserve the gesture
# from frame 60 through 112 (52 intervals at 60 Hz, approximately 0.867 s).
THROW_POSITIONS = (0, 20, 28, 36, 44, 52, 60, 76, 96, 112, 120, 128, 136, 142, 148, 168)
REACTION_POSITIONS = (0, 16, 24, 32, 40, 48, 56, 64, 72, 80, 90, 100, 108, 116, 124, 144)
RPS_TIMES = {
    **{clip: (2.8, THROW_POSITIONS) for clip in ("RpsRock", "RpsPaper", "RpsScissors")},
    **{clip: (2.4, REACTION_POSITIONS) for clip in ("RpsWin", "RpsLose")},
}
