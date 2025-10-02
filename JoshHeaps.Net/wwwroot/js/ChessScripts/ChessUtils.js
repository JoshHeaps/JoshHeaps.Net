const ChessUtils = {
    flipCoordinates(row, col) {
        if (!GameState.currentPlayerIsWhite) {
            return [7 - row, 7 - col];
        }
        return [row, col];
    },

    getCookie(name) {
        const value = document.cookie.split('; ')
            .find(row => row.startsWith(name + '='));
        return value ? value.split('=')[1] : null;
    },

    setCookie(name, value, maxAge = 86400) {
        document.cookie = `${name}=${value}; path=/; max-age=${maxAge}`;
    },

    getPieceImageUrl(piece) {
        const basePath = "/images/Chess Images/";
        const color = piece.color === 0 ? "White" : "Black";
        const typeMap = {
            0: "Pawn",
            1: "Rook",
            2: "Knight",
            3: "Bishop",
            4: "Queen",
            5: "King"
        };

        return basePath + color + typeMap[piece.type] + ".svg";
    },

    updatePromotionModalImages(color) {
        const pieceNames = ["Queen", "Rook", "Bishop", "Knight"];
        const buttons = document.querySelectorAll("#promotionModal button img");

        buttons.forEach((img, index) => {
            img.src = `/images/Chess Images/${color === 0 ? "White" : "Black"}${pieceNames[index]}.svg`;
        });
    }
};

console.log("ChessUtils.js loaded");