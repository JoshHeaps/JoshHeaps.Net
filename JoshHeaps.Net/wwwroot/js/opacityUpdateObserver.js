let thresholds = [];
for (let i = 0; i <= 1.0; i += 0.01) {
    thresholds.push(i);
}

const percentThresholdsOptions = {
    root: null,
    threshold: thresholds,
};

const updateOpacityCallback = (entries) => {
    entries.forEach(entry => {
        entry.target.style.opacity = entry.intersectionRatio;
    });
};

const opacityObserver = new IntersectionObserver(updateOpacityCallback, percentThresholdsOptions);

window.addEventListener('load', createObserver);

function createObserver() {
    const targetImages = document.querySelectorAll(".projectImage");

    targetImages.forEach(targetImage => {
        opacityObserver.observe(targetImage);
    });
};