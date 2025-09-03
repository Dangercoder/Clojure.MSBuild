(ns main-test
  (:require [clojure.test :refer :all]
            [main :as main]))

(deftest test-placeholder
  (testing "Basic test setup"
    (is (= 1 1) "Math still works")))