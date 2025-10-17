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
const punctuationDict = {
    '.': 150,
    '!': 200,
    '?': 200,
    ',': 100,
    '`': 25,
};

let currentText = "";

function changeIntervalTime(element, text) {
    clearInterval(intervalId);
    simulateTyping(element, text, punctuationDict[text.charAt(index)] ?? 50);
}

function simulateTyping(element, text, speed = 50) {
    intervalId = setInterval(() => {
        if (index < text.length) {
            if (text.charAt(index) === '`')
                currentText = currentText.slice(0, -1);
            else
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
    else if (option === 'demos') {
        document.querySelector("#DemosBox").scrollIntoView({
            behavior: "smooth",
            block: "start",
            inline: "nearest"
        });
    }
}