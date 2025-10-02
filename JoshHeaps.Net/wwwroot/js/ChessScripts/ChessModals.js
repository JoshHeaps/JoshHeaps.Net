const ChessModals = {
    promptPromotion() {
        return new Promise(resolve => {
            let color = 1;
            if (GameState.currentPlayerIsWhite)
                color = 0;

            ChessUtils.updatePromotionModalImages(color);

            document.getElementById("promotionModal").style.display = "block";
            window.selectPromotion = (piece) => {
                document.getElementById("promotionModal").style.display = "none";
                resolve(piece);
            };
        });
    },

    promptDifficulty() {
        return new Promise(resolve => {
            document.getElementById("difficultyModal").style.display = "block";
            window.selectDifficulty = (difficulty) => {
                document.getElementById("difficultyModal").style.display = "none";
                resolve(difficulty);
            };
        });
    }
};

console.log("ChessModals.js loaded");