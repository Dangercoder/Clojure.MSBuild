(ns main-test
  (:require [clojure.test :refer :all]
            [main :refer [get-status]]))

(deftest test-get-status
  (testing "get-status returns success"
    (let [result (get-status)]
      (is (= :success (:status result)))
      (is (map? result))
      (is (= {:status :success} result)))))