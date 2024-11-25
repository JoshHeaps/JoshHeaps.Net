function typeText(selector, content) {
    const element = document.querySelector(selector);

    if (!element) {
        console.log("Element not found.");
        return;
    }

    simulateTyping(element, content);
}

let intervalId;
let index = 0;
const punctuation = ['.', '!', '?'];

let currentText = "";

function changeIntervalTime(element, text) {
    if (punctuation.includes(text.charAt(index))) {
        clearInterval(intervalId);
        simulateTyping(element, text, 200);
    }
    else {
        clearInterval(intervalId);
        simulateTyping(element, text);
    }
}

function simulateTyping(element, text, speed = 50) {
    intervalId = setInterval(() => {
        if (index < text.length) {
            currentText += text.charAt(index);
            changeIntervalTime(element, text);
            index++;
            element.textContent = currentText;
        } else {
            clearInterval(intervalId);
        }
    }, speed);
}

function showClickedContents(option) {
    if (option === 'projects') {
        document.querySelector("#ChessProject > div.diagonal-section-left").scrollIntoView({
            behavior: "smooth",
            block: "center",
            inline: "nearest"
        });
    }
    else if (option === 'contact') {
        navigator.clipboard.writeText('435-890-2957').then(() => {
            console.log('copied');
        });
        document.querySelector("#Contacts").scrollIntoView({
            behavior: "smooth",
            block: "start",
            inline: "nearest"
        });
    }
}